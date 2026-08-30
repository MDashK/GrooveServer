using GrooveServer.Net;

namespace GrooveServer.Tests;

/// <summary>
/// O XP de cada nivel, contra a barra do cliente.
/// </summary>
public class PlayerProfileTests
{
    /// <summary>Como o cliente a mostra: truncada nas centesimas, nao arredondada.</summary>
    private static double Truncada(double pc) => Math.Truncate(pc * 100) / 100;

    /// <summary>
    /// Os dois pontos medidos. O nivel do jogo e' o interno mais um, por isso o 9 do ecra e'
    /// o 8 aqui e o 13 e' o 12.
    /// </summary>
    /// <summary>
    /// 740 pontos no nivel 13 davam 37,75% no ecra, e 778 davam 39,69%. Com os 840 antigos o
    /// servidor dizia 88% e 92%.
    /// </summary>
    [Theory]
    [InlineData(740, 37.75)]
    [InlineData(778, 39.69)]
    public void APercentagemBateComOEcra(int xp, double esperado)
    {
        var p = new PlayerProfile(12, xp, 0);
        Assert.Equal(esperado, Truncada(p.XpPercent));
    }

    /// <summary>
    /// As leituras com 1000 pontos, uma por nivel. O cliente trunca nas centesimas, por isso
    /// compara-se so' ate' a' primeira casa.
    /// </summary>
    [Theory]
    [InlineData(10, 79.36)]   // ecra: nivel 11
    [InlineData(11, 64.93)]   // ecra: nivel 12
    [InlineData(12, 51.02)]   // ecra: nivel 13
    [InlineData(13, 42.01)]   // ecra: nivel 14
    [InlineData(15, 31.05)]   // ecra: nivel 16
    public void MilPontosDaoAPercentagemDoEcra(int nivelBaseZero, double esperado)
    {
        var p = new PlayerProfile(nivelBaseZero, 1000, 0);
        Assert.Equal(esperado, Truncada(p.XpPercent));
    }

    /// <summary>
    /// O XP que o servidor REAL deu, lido do +37 do 0x0070 em nove gravacoes. Aceita-se um
    /// ponto de erro: dentro de cada grupo sobra variacao que nao esta' no que o cliente envia.
    /// </summary>
    [Theory]
    [InlineData(0, 38)]
    [InlineData(1, 28)]
    [InlineData(2, 27)]
    [InlineData(4, 27)]
    [InlineData(5, 26)]
    [InlineData(6, 25)]
    [InlineData(9, 26)]
    public void OXpDaJogadaBateComOServidorReal(int breaks, int real) =>
        Assert.InRange(PlayerProfile.XpDaJogada(95f, breaks), real - 1, real + 1);

    /// <summary>
    /// A tabela do cliente, em 0x1575a0 do dump. Estes seis foram medidos contra a barra do
    /// perfil antes de a tabela aparecer, e batem todos.
    /// </summary>
    [Theory]
    [InlineData(8, 840)]     // ecra: 9
    [InlineData(9, 980)]     // ecra: 10
    [InlineData(10, 1260)]   // ecra: 11
    [InlineData(11, 1540)]   // ecra: 12
    [InlineData(12, 1960)]   // ecra: 13
    [InlineData(13, 2380)]   // ecra: 14
    [InlineData(14, 2800)]   // ecra: 15
    [InlineData(15, 3220)]   // ecra: 16
    [InlineData(0, 40)]      // ecra: 1
    [InlineData(97, 232540000)]  // ecra: 98, o ultimo com limiar
    public void OXpDoNivelVemDaTabelaDoCliente(int nivelBaseZero, int esperado) =>
        Assert.Equal(esperado, PlayerProfile.XpDoNivel(nivelBaseZero));

    /// <summary>No nivel 99 do ecra ja' nao se ganha XP; o MAX continua a entrar.</summary>
    [Fact]
    public void NoTectoNaoSeGanhaMaisExperiencia()
    {
        var p = new PlayerProfile(PlayerProfile.NivelMaximo, 0, 100);
        Assert.False(p.CompleteSong(maxGanho: 20, xpGanho: 999));
        Assert.Equal(PlayerProfile.NivelMaximo, p.Level);
        Assert.Equal(0, p.Xp);
        Assert.Equal(120, p.Max);
    }

    /// <summary>A subir de varios niveis de uma vez, o tecto trava e nao deixa XP pendurado.</summary>
    [Fact]
    public void OTectoTravaMesmoComMuitoXpDeUmaVez()
    {
        var p = new PlayerProfile(PlayerProfile.NivelMaximo - 1, 0, 0);
        p.CompleteSong(maxGanho: 1, xpGanho: int.MaxValue / 2);
        Assert.Equal(PlayerProfile.NivelMaximo, p.Level);
        Assert.Equal(0, p.Xp);
    }

    [Fact]
    public void SubirDeNivelUsaOLimiarDoNivelEmQueEsta()
    {
        var p = new PlayerProfile(12, 1950, 0);
        Assert.True(p.CompleteSong(maxGanho: 10, xpGanho: 20));
        Assert.Equal(13, p.Level);
        Assert.Equal(10, p.Xp);        // 1950 + 20 - 1960
    }
}
