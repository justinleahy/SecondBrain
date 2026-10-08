using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SecondBrain.Extractor;

/// <summary>Small 64-bit Linux/Darwin sendmsg/recvmsg prototype; no native shim or parser dependency.</summary>
public static class UnixDescriptorTransport
{
    private const int ScmRights = 1;
    private const int Interrupted = 4;
    private const int MaximumDescriptors = 16;
    private static bool Darwin => OperatingSystem.IsMacOS();
    private static int Alignment => Darwin ? 4 : IntPtr.Size;
    private static int ControlHeaderSize => Darwin ? 12 : IntPtr.Size + 8;
    private static int SocketLevel => Darwin ? 0xffff : 1;
    private static int NonBlockingFlag => Darwin ? 0x80 : 0x40;
    private static int NoSignalFlag => Darwin ? 0x80000 : 0x4000;
    private static int ControlTruncatedFlag => Darwin ? 0x20 : 0x08;

    internal static void PreventInheritance(SafeHandle handle)
    {
        var retained = false;
        try
        {
            handle.DangerousAddRef(ref retained);
            var fd = checked((int)handle.DangerousGetHandle());
            var flags = NativeMethods.FcntlGet(fd, 1); // F_GETFD has no variadic argument.
            var setResult = flags == -1 ? -1 : Darwin && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? NativeMethods.FcntlSetDarwinArm64(fd, 2, 0, 0, 0, 0, 0, 0, flags | 1)
                : NativeMethods.FcntlSet(fd, 2, flags | 1);
            var actualFlags = setResult == -1 ? -1 : NativeMethods.FcntlGet(fd, 1);
            if (actualFlags < 0 || (actualFlags & 1) != 1)
            {
                throw new IOException("Cannot prevent descriptor inheritance.");
            }
        }
        finally
        {
            if (retained)
            {
                handle.DangerousRelease();
            }
        }
    }

    public static void Send(Socket socket, ReadOnlySpan<byte> json, SafeFileHandle? descriptor = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSupported(socket);
        if (json.IsEmpty || json.Length > ExtractorProtocol.MaximumFrameBytes)
        {
            throw new InvalidDataException("Extractor frames must contain 1 to 65536 JSON bytes.");
        }

        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)json.Length);
        var descriptorRetained = false;
        try
        {
            descriptor?.DangerousAddRef(ref descriptorRetained);
            var fd = descriptor is null ? (int?)null : checked((int)descriptor.DangerousGetHandle());
            SendAll(socket, prefix, fd, cancellationToken);
            SendAll(socket, json.ToArray(), null, cancellationToken);
        }
        finally
        {
            if (descriptorRetained)
            {
                descriptor!.DangerousRelease();
            }
        }
    }

    public static ExtractorFrame Receive(Socket socket, CancellationToken cancellationToken = default)
    {
        EnsureSupported(socket);
        var descriptors = new List<SafeFileHandle>();
        try
        {
            var prefix = new byte[4];
            ReceiveAll(socket, prefix, descriptors, allowDescriptor: true, cancellationToken);
            var length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
            if (length is 0 or > ExtractorProtocol.MaximumFrameBytes)
            {
                throw new InvalidDataException("Extractor frame length is outside the 1..65536 byte bound.");
            }

            var body = new byte[(int)length];
            ReceiveAll(socket, body, descriptors, allowDescriptor: false, cancellationToken);
            return new ExtractorFrame(body, descriptors.SingleOrDefault());
        }
        catch
        {
            foreach (var descriptor in descriptors)
            {
                descriptor.Dispose();
            }
            throw;
        }
    }

    public static void RequireReadOnlyRegularFile(SafeFileHandle descriptor)
    {
        var retained = false;
        var statBuffer = Marshal.AllocHGlobal(512);
        try
        {
            descriptor.DangerousAddRef(ref retained);
            var fd = checked((int)descriptor.DangerousGetHandle());
            var flags = NativeMethods.FcntlGet(fd, 3); // F_GETFL consumes no variadic argument.
            if (flags == -1 || NativeMethods.Fstat(fd, statBuffer) == -1)
            {
                throw new IOException("Cannot inspect the staged descriptor.");
            }

            // stat.st_mode: Darwin offset 4 (ushort), Linux x64 offset 24, Linux arm64 offset 16.
            var mode = Darwin ? (uint)(ushort)Marshal.ReadInt16(statBuffer, 4)
                : (uint)Marshal.ReadInt32(statBuffer, RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 16 : 24);
            if ((flags & 3) != 0 || (mode & 0xf000) != 0x8000)
            {
                throw new InvalidDataException("The extractor accepts only a read-only regular-file descriptor.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(statBuffer);
            if (retained)
            {
                descriptor.DangerousRelease();
            }
        }
    }

    private static void SendAll(Socket socket, byte[] bytes, int? descriptor, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            WaitFor(socket, SelectMode.SelectWrite, cancellationToken);
            var result = InvokeMessage(socket, bytes, offset, descriptor, receive: false, null);
            if (result == -1)
            {
                RetryOrThrow();
                continue;
            }
            if (result == 0)
            {
                throw new EndOfStreamException("The extractor peer closed during send.");
            }
            offset += checked((int)result);
            descriptor = null; // Ancillary data is attached once, to the first successfully sent byte.
        }
    }

    private static void ReceiveAll(Socket socket, byte[] bytes, List<SafeFileHandle> descriptors,
        bool allowDescriptor, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            WaitFor(socket, SelectMode.SelectRead, cancellationToken);
            var before = descriptors.Count;
            var result = InvokeMessage(socket, bytes, offset, null, receive: true, descriptors);
            if (result == -1)
            {
                RetryOrThrow();
                continue;
            }
            if (result == 0)
            {
                throw new EndOfStreamException("The extractor peer closed before the frame was complete.");
            }
            if (descriptors.Count > 1 || (descriptors.Count != before && (!allowDescriptor || offset != 0)))
            {
                throw new InvalidDataException("A single descriptor must be attached to the first prefix byte only.");
            }
            offset += checked((int)result);
        }
    }

    private static long InvokeMessage(Socket socket, byte[] bytes, int offset, int? descriptor,
        bool receive, List<SafeFileHandle>? receivedDescriptors)
    {
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var vector = Marshal.AllocHGlobal(Marshal.SizeOf<IoVector>());
        var message = Marshal.AllocHGlobal(Darwin ? Marshal.SizeOf<DarwinMessage>() : Marshal.SizeOf<LinuxMessage>());
        var controlSize = Align(ControlHeaderSize) + Align(sizeof(int) * (receive ? MaximumDescriptors : 1));
        var control = Marshal.AllocHGlobal(controlSize);
        var socketRetained = false;
        try
        {
            Marshal.Copy(new byte[controlSize], 0, control, controlSize);
            var controlLength = receive ? controlSize : descriptor.HasValue ? controlSize : 0;
            if (descriptor.HasValue)
            {
                if (Darwin)
                {
                    Marshal.WriteInt32(control, ControlHeaderSize + sizeof(int));
                }
                else
                {
                    Marshal.WriteInt64(control, ControlHeaderSize + sizeof(int));
                }
                Marshal.WriteInt32(control, Darwin ? 4 : IntPtr.Size, SocketLevel);
                Marshal.WriteInt32(control, (Darwin ? 4 : IntPtr.Size) + 4, ScmRights);
                Marshal.WriteInt32(control, Align(ControlHeaderSize), descriptor.Value);
            }

            Marshal.StructureToPtr(new IoVector
            {
                Base = IntPtr.Add(pin.AddrOfPinnedObject(), offset),
                Length = (nuint)(bytes.Length - offset)
            }, vector, false);
            if (Darwin)
            {
                Marshal.StructureToPtr(new DarwinMessage
                {
                    Vectors = vector, VectorCount = 1, Control = controlLength == 0 ? IntPtr.Zero : control,
                    ControlLength = (uint)controlLength
                }, message, false);
            }
            else
            {
                Marshal.StructureToPtr(new LinuxMessage
                {
                    Vectors = vector, VectorCount = 1, Control = controlLength == 0 ? IntPtr.Zero : control,
                    ControlLength = (nuint)controlLength
                }, message, false);
            }

            socket.SafeHandle.DangerousAddRef(ref socketRetained);
            var fd = checked((int)socket.SafeHandle.DangerousGetHandle());
            // Linux can atomically prevent SCM_RIGHTS inheritance; Darwin receives then applies fcntl.
            var result = receive ? NativeMethods.ReceiveMessage(fd, message, NonBlockingFlag | (Darwin ? 0 : 0x40000000))
                : NativeMethods.SendMessage(fd, message, NonBlockingFlag | NoSignalFlag);
            if (receive && result >= 0)
            {
                var returnedLength = Darwin ? Marshal.PtrToStructure<DarwinMessage>(message).ControlLength
                    : (ulong)Marshal.PtrToStructure<LinuxMessage>(message).ControlLength;
                var flags = Darwin ? Marshal.PtrToStructure<DarwinMessage>(message).Flags
                    : Marshal.PtrToStructure<LinuxMessage>(message).Flags;
                ReadDescriptors(control, checked((int)returnedLength), receivedDescriptors!);
                if ((flags & ControlTruncatedFlag) != 0)
                {
                    throw new InvalidDataException("The extractor rejects truncated ancillary data.");
                }
            }
            return result;
        }
        finally
        {
            if (socketRetained)
            {
                socket.SafeHandle.DangerousRelease();
            }
            Marshal.FreeHGlobal(control);
            Marshal.FreeHGlobal(message);
            Marshal.FreeHGlobal(vector);
            pin.Free();
        }
    }

    private static void ReadDescriptors(IntPtr control, int length, List<SafeFileHandle> descriptors)
    {
        var offset = 0;
        while (offset + ControlHeaderSize <= length)
        {
            var current = IntPtr.Add(control, offset);
            var size = Darwin ? (uint)Marshal.ReadInt32(current) : (ulong)Marshal.ReadInt64(current);
            if (size < (ulong)Align(ControlHeaderSize) || size > (ulong)(length - offset))
            {
                throw new InvalidDataException("Invalid ancillary message size.");
            }
            var level = Marshal.ReadInt32(current, Darwin ? 4 : IntPtr.Size);
            var type = Marshal.ReadInt32(current, (Darwin ? 4 : IntPtr.Size) + 4);
            if (level == SocketLevel && type == ScmRights)
            {
                var dataSize = checked((int)size) - Align(ControlHeaderSize);
                if (dataSize % sizeof(int) != 0)
                {
                    throw new InvalidDataException("Invalid descriptor message size.");
                }
                for (var position = Align(ControlHeaderSize); position < (int)size; position += sizeof(int))
                {
                    var fd = Marshal.ReadInt32(current, position);
                    var handle = new SafeFileHandle(fd, ownsHandle: true);
                    descriptors.Add(handle);
                }
            }
            offset += Align(checked((int)size));
        }
        foreach (var descriptor in descriptors)
        {
            PreventInheritance(descriptor);
        }
    }

    private static int Align(int size) => (size + Alignment - 1) & ~(Alignment - 1);

    private static void WaitFor(Socket socket, SelectMode mode, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (socket.Poll(100_000, mode))
            {
                return;
            }
        }
    }

    private static void RetryOrThrow()
    {
        var error = Marshal.GetLastPInvokeError();
        if (error != Interrupted && error != (Darwin ? 35 : 11))
        {
            throw new SocketException(error);
        }
    }

    private static void EnsureSupported(Socket socket)
    {
        if ((!OperatingSystem.IsLinux() && !Darwin) || IntPtr.Size != 8
            || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            throw new PlatformNotSupportedException("Descriptor transport supports 64-bit Linux and macOS.");
        }
        if (socket.AddressFamily != AddressFamily.Unix || socket.SocketType != SocketType.Stream)
        {
            throw new ArgumentException("Extractor transport requires a Unix stream socket.", nameof(socket));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVector
    {
        public IntPtr Base;
        public nuint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxMessage
    {
        public IntPtr Name;
        public uint NameLength;
        public IntPtr Vectors;
        public nuint VectorCount;
        public IntPtr Control;
        public nuint ControlLength;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinMessage
    {
        public IntPtr Name;
        public uint NameLength;
        public IntPtr Vectors;
        public int VectorCount;
        public IntPtr Control;
        public uint ControlLength;
        public int Flags;
    }

    private static class NativeMethods
    {
        [DllImport("libc", EntryPoint = "sendmsg", SetLastError = true)]
        public static extern nint SendMessage(int socket, IntPtr message, int flags);

        [DllImport("libc", EntryPoint = "recvmsg", SetLastError = true)]
        public static extern nint ReceiveMessage(int socket, IntPtr message, int flags);

        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        public static extern int FcntlGet(int descriptor, int command);

        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        public static extern int FcntlSet(int descriptor, int command, int argument);

        // Apple's arm64 ABI passes every variadic argument on the stack, unlike fixed P/Invoke arguments.
        // Six unused 64-bit slots occupy x2..x7 so the actual flag is passed at [sp], matching Clang's
        // fcntl(fd, F_SETFD, flags) output. The fixed parameters remain w0=fd and w1=command.
        // https://developer.apple.com/documentation/xcode/writing-arm64-code-for-apple-platforms
        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
        public static extern int FcntlSetDarwinArm64(int descriptor, int command,
            nint register2, nint register3, nint register4, nint register5, nint register6, nint register7,
            nint argument);

        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        public static extern int Fstat(int descriptor, IntPtr stat);
    }
}
