namespace GrooveServer.Protocol;

/// <summary>
/// O PREMIO DE ITEM DA CONCLUSAO DE UM COURSE — o sorteio que o ecra final anuncia, e que
/// sai "X" quando nao calha nada.
///
/// Sao duas mensagens, medidas na course2_s1 e na course_s1 (as duas do "Let's Begin"):
///
///     0x0089  cabecalho 89 00 1c | 01 f5 | 01 00     corpo c7 27 35 01 cc cc ...
///                                  ^^^^^   ^^^^^           ^^^^^^^^^^^
///                                  item    quantidade      data AAAAMMDD
///
/// <c>0x01F5</c> lido em little-endian e' <c>0xF501</c> = 62721, e e' um dos dois itens que
/// o CourseSection.ini poe' no "Let's Begin" (<c>item=62465,30</c> e <c>item=62721,20</c>).
/// A data <c>0x01352 7C7</c> = 20260807 e' a do dia da gravacao.
///
/// Logo a seguir o cliente pede o inventario (<c>0x008A</c>) e recebe o
/// <c>0x002C</c>, 247 B, cujo corpo e' a lista de itens possuidos no formato do
/// <see cref="InventoryCodec"/> — 30 lugares de 8 bytes, o resto a 0xFF:
///
///     20 88 01 00  56 8a 34 01     100384   instancia 20220502
///     06 8c 01 00  c6 27 35 01     101382   instancia 20260806
///     04 84 01 00  c7 27 35 01      99332   instancia 20260807
///     02 a4 01 00  c7 27 35 01     107010   instancia 20260807
///     01 f5 02 00  c7 27 35 01     193793   instancia 20260807   <- o premio
///     07 a8 01 00  c7 27 35 01     108039   instancia 20260807
///
/// DUAS COISAS QUE ISTO ENSINA:
///
/// 1. O id de catalogo tem 32 bits e o <c>0x0089</c> so' leva os 16 BAIXOS. O premio
///    62721 = <c>0xF501</c> aparece no inventario como <c>0x0002F501</c> = 193793. O 2 e' a
///    "section1" do ItemStock.csv, que vale 2 em toda a gama baixa de ids — inclusive nos
///    1053 e 2065 que aparecem como premio noutros courses. Medido num item, inferido nos
///    outros; se algum dia sair um item errado no inventario, e' aqui que se olha.
///
/// 2. A INSTANCIA E' A DATA, nao um numero de serie. Os quatro itens ganhos no mesmo dia
///    levam todos 20260807. Nao ha' nada de unico por copia.
/// </summary>
public static class CoursePrize
{
    /// <summary>O servidor anuncia o item ganho.</summary>
    public const ushort MessageId = 0x0089;

    /// <summary>Metade baixa do id de catalogo, no cabecalho.</summary>
    public const int ItemOffset = 3;      // u16

    /// <summary>Quantas copias, no cabecalho.</summary>
    public const int QuantidadeOffset = 5;   // u16

    /// <summary>Data da atribuicao, AAAAMMDD, no inicio do corpo.</summary>
    public const int DataOffset = 0;      // u32

    /// <summary>A metade alta do id de catalogo destes itens.</summary>
    public const uint SeccaoAlta = 2;

    public static uint CatalogoCompleto(uint baixo) => (SeccaoAlta << 16) | (baixo & 0xFFFF);

    public static ushort MetadeBaixa(uint catalogo) => (ushort)(catalogo & 0xFFFF);

    /// <summary>A data na forma em que o jogo a grava: AAAAMMDD.</summary>
    public static uint Data(DateTime quando) =>
        (uint)(quando.Year * 10000 + quando.Month * 100 + quando.Day);

    public static bool PodeEscrever(byte[] cabecalho) => cabecalho.Length >= QuantidadeOffset + 2;

    public static void Escrever(byte[] cabecalho, byte[] corpo, uint catalogo, int quantidade, DateTime quando)
    {
        BitConverter.TryWriteBytes(cabecalho.AsSpan(ItemOffset, 2), MetadeBaixa(catalogo));
        BitConverter.TryWriteBytes(cabecalho.AsSpan(QuantidadeOffset, 2), (ushort)quantidade);
        if (corpo.Length >= DataOffset + 4)
            BitConverter.TryWriteBytes(corpo.AsSpan(DataOffset, 4), Data(quando));
    }

    /// <summary>A lista de itens possuidos que o <c>0x008A</c> vai buscar.</summary>
    public const ushort InventarioId = 0x002C;
}
