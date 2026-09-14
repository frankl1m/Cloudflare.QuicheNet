namespace Cloudflare.QuicheNet;

public enum QuicheCcAlgorithm
{
    QUICHE_CC_RENO = 0,
    QUICHE_CC_CUBIC = 1,
    [Obsolete("Removed from quiche; mapped to QUICHE_CC_BBR2_GCONGESTION.")]
    QUICHE_CC_BBR = 2,
    [Obsolete("Removed from quiche; mapped to QUICHE_CC_BBR2_GCONGESTION.")]
    QUICHE_CC_BBR2 = 3,
    QUICHE_CC_BBR2_GCONGESTION = 4,
    QUICHE_CC_BRUTAL = 5,
}
