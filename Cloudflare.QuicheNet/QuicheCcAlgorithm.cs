namespace Cloudflare.QuicheNet;

/// <summary>
/// Los controles de congestión de quiche, con los valores de su enum en Rust. La FFI toma
/// el enum por valor, y un número que Rust no conoce llega como discriminante inválido: por
/// eso la configuración valida los valores y traduce los alias antiguos. BBR y BBR2 (2 y 3) desaparecieron en quiche 0.29; Brutal
/// es el de la DLL parcheada.
/// </summary>
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
