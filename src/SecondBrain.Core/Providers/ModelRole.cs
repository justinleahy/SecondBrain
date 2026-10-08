namespace SecondBrain.Core.Providers;

/// <summary>Provider-neutral roles bound to configured providers and models; see spec §13.1.</summary>
public enum ModelRole
{
    /// <summary>Assistant turns and grounded answers (§13.1).</summary>
    Chat,

    /// <summary>Enrichment, classification and optional LLM reranking (§13.1).</summary>
    Enrich,

    /// <summary>Document and query embeddings (§13.1).</summary>
    Embed,

    /// <summary>Optional second-stage ranking (§13.1).</summary>
    Rerank,
}
