using GrooveServer.Protocol;

namespace GrooveServer.Tests;

/// <summary>
/// Valida o esquema da tabela de high scores de um course contra bytes reais.
///
/// Vetor: gravacoes/course2_s1.txt, o <c>0x0084</c> do course 0 ("Let's Begin"), capturado
/// contra 101.32.26.152:23505. A tabela tinha quatro jogadores e bate com o que o ecra do
/// jogo mostrava.
/// </summary>
public class CourseRankTests
{
    /// <summary>Cabecalho em claro: 84 00 | chave | course 0 | id 360 do primeiro lugar.</summary>
    private const string CabecalhoHex = "84005b00006801";

    /// <summary>Os primeiros 96 bytes do corpo decifrado.</summary>
    private const string CorpoHex =
        "00004d446173684b0000000000000000" +
        "000000000000000000000000003b620b" +
        "0092010000c72735011209000043484e" +
        "5f4c3700000000000000000000000000" +
        "0000000000000000e8fb0a0092010000" +
        "742735015b0900006363733030323538";

    [Fact]
    public void CabemCinquentaLugaresNoCorpoReal()
    {
        // 41 + 49 x 43 = 2148. E' o encaixe exato que mostra que o id do primeiro lugar
        // viaja no cabecalho e nao no corpo.
        Assert.Equal(50, CourseRank.Lugares(2148));
    }

    [Fact]
    public void EscreverReproduzOsBytesGravados()
    {
        var esperado = Convert.FromHexString(CorpoHex);
        var cabecalho = Convert.FromHexString(CabecalhoHex);

        var corpo = new byte[2148];
        ushort primeiro = CourseRank.Escrever(corpo, new[]
        {
            new CourseRank.Entrada(360,  "MDashK",   746043, 402, 20260807),
            new CourseRank.Entrada(2322, "CHN_L7",   719848, 402, 20260724),
            // O terceiro entra so' pelo id e pelo nome: os numeros dele caem depois do
            // pedaco que este vetor cobre.
            new CourseRank.Entrada(2395, "ccs00258", 0,      0,   0),
        });

        Assert.Equal(esperado, corpo.AsSpan(0, esperado.Length).ToArray());

        // E o id do primeiro lugar e' o que vai no cabecalho, em [5..6].
        Assert.Equal(BitConverter.ToUInt16(cabecalho, CourseRank.HeaderIdOffset), primeiro);
        Assert.Equal(0, BitConverter.ToUInt16(cabecalho, CourseRank.HeaderCourseOffset));
    }

    [Fact]
    public void TabelaVaziaDaIdZero()
    {
        var corpo = new byte[2148];
        Array.Fill(corpo, (byte)0x5A);
        Assert.Equal(0, CourseRank.Escrever(corpo, Array.Empty<CourseRank.Entrada>()));
        Assert.All(corpo, b => Assert.Equal(0, b));
    }
}
