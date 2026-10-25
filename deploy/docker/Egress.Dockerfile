FROM cgr.dev/chainguard/wolfi-base@sha256:360896efb0753a5701c48f860e17130c205eae5a264797aa3e7ea126ee2923a7
RUN apk add --no-cache iptables=1.8.13-r5 iproute2=7.3.0-r0
# Select the nft backend explicitly; the host need not load legacy iptables modules.
RUN for tool in iptables iptables-save iptables-restore ip6tables ip6tables-save ip6tables-restore; do \
      ln -sf xtables-nft-multi "/usr/bin/$tool"; \
    done
COPY --chmod=0555 deploy/docker/egress-entrypoint.sh /usr/local/bin/egress-entrypoint
USER 0:0
ENTRYPOINT ["/usr/local/bin/egress-entrypoint"]
