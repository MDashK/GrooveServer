using GrooveServer.Net;

namespace GrooveServer.Tests;

/// <summary>
/// A economia do course: o que se paga a' entrada, o que se paga a continuar, e o bonus da
/// conclusao. Os tres numeros vem do `CourseSection.ini` do jogo, ja' no `courses.txt`.
/// </summary>
public class CourseEconomiaTests
{
    private static ItemTable Tabela(params string[] linhas)
    {
        var f = Path.GetTempFileName();
        File.WriteAllLines(f, linhas);
        try { return ItemTable.Load(f); } finally { File.Delete(f); }
    }

    /// <summary>
    /// O bonus de conclusao e' uma PERCENTAGEM do MAX ganho no course inteiro. Os valores
    /// sao os do "Let's Begin" (50%), "Step by Step" (60%) e "Yo! MAX!" (80%).
    /// </summary>
    [Theory]
    [InlineData(50, 35, 18)]     // 17,5 arredonda para 18
    [InlineData(60, 35, 21)]
    [InlineData(80, 53, 42)]     // 42,4
    [InlineData(70, 0, 0)]       // sem MAX ganho nao ha' bonus
    [InlineData(0, 53, 0)]       // course sem bonus declarado
    public void OBonusDeConclusaoEUmaPercentagem(int bonusPct, int maxDoCourse, int esperado) =>
        Assert.Equal(esperado, (int)Math.Round(maxDoCourse * (bonusPct / 100.0)));

    /// <summary>
    /// O saldo nunca fica negativo: entrar num course que custa mais do que se tem deixa a
    /// conta a zero, nao a numeros negativos que o protocolo nao sabe levar (o saldo viaja
    /// em u16 no ack de compra).
    /// </summary>
    [Theory]
    [InlineData(500, 100, 400)]
    [InlineData(50, 100, 0)]
    [InlineData(0, 220, 0)]
    public void OPrecoDoCourseNaoDeixaOSaldoNegativo(int saldo, int preco, int esperado) =>
        Assert.Equal(esperado, Math.Max(0, saldo - preco));

    /// <summary>
    /// Os MAX Boosters nao tem o efeito nos dados do cliente — as tres linhas do ItemStock
    /// tem as colunas todas a zero. A tabela esta' no codigo, com os numeros da loja.
    /// </summary>
    [Theory]
    [InlineData(62721u, 50)]
    [InlineData(62722u, 200)]
    [InlineData(62723u, 500)]
    [InlineData(62593u, 0)]      // EXP Booster: nao e' de MAX
    [InlineData(35846u, 0)]      // um avatar qualquer
    public void OMaxBoosterTemAPercentagemDaLoja(uint catalogo, double esperado) =>
        Assert.Equal(esperado, ItemTable.PercentagemDoMaxBooster(catalogo));

    /// <summary>Procura-se pelos 16 bits BAIXOS, como no resto do inventario.</summary>
    [Fact]
    public void OMaxBoosterEAchadoPelaMetadeBaixaDoCatalogo() =>
        Assert.Equal(200, ItemTable.PercentagemDoMaxBooster((2u << 16) | 62722u));

    // O `BonusDeUsoUnico` foi-se, e o teste dele com ele. Era o resto da teoria de que os
    // boosters se "usavam" — que a medicao desmentiu, e que o teste a seguir e' quem cobre.

    /// <summary>
    /// O BOOSTER EQUIPA-SE, NAO SE "USA". Medido no jogo: o botao dele e' o 装備, o inventario
    /// mostra-o com 装備中, e uma sessao inteira de free mode e course nao produziu um unico
    /// `0x00BA`.
    ///
    /// Sem contar a percentagem que falta nos dados, o MAX Booster equipado somava ZERO e
    /// parecia avariado — foi exactamente o que aconteceu no primeiro teste.
    /// </summary>
    [Fact]
    public void OMaxBoosterEquipadoSomaAPercentagem()
    {
        var t = Tabela("62721\tMAX_Boost_LV1\texp=0\tmaxpc=0\tmaxfix=0\thp=0\tdias=0\tuso=1");
        var equipado = new[]
        {
            // o id completo traz a seccao na metade alta, como no inventario real
            new UserStore.Item { CatalogId = 193793, InstanceId = 1, Equipped = true },
        };
        Assert.Equal((0.0, 50.0, 0.0, 0), t.Bonus(equipado));
    }

    /// <summary>Guardado e nao equipado, nao conta.</summary>
    [Fact]
    public void OMaxBoosterDesequipadoNaoConta()
    {
        var t = Tabela("62721\tMAX_Boost_LV1\texp=0\tmaxpc=0\tmaxfix=0\thp=0\tdias=0\tuso=1");
        var guardado = new[]
        {
            new UserStore.Item { CatalogId = 193793, InstanceId = 1, Equipped = false },
        };
        Assert.Equal((0.0, 0.0, 0.0, 0), t.Bonus(guardado));
    }

    /// <summary>
    /// O EXP Booster ja' funcionava, porque tem o `exp=50` na coluna certa do CSV. Foi a
    /// comparacao entre os dois que denunciou o buraco do MAX.
    /// </summary>
    [Fact]
    public void OsDoisBoostersEquipadosSomamCadaUmOSeu()
    {
        var t = Tabela("62593\tEXP_Boost_LV1\texp=50\tmaxpc=0\tmaxfix=0\thp=0\tdias=0\tuso=1",
                       "62721\tMAX_Boost_LV1\texp=0\tmaxpc=0\tmaxfix=0\thp=0\tdias=0\tuso=1");
        var equipados = new[]
        {
            new UserStore.Item { CatalogId = 62593, InstanceId = 1, Equipped = true },
            new UserStore.Item { CatalogId = 62721, InstanceId = 2, Equipped = true },
        };
        Assert.Equal((50.0, 50.0, 0.0, 0), t.Bonus(equipados));
    }

    /// <summary>
    /// O ECRA MOSTRA O QUE FOI CONCEDIDO. Uma etapa que reporta 13 de MAX cru, com gear de
    /// +20% e +20 fixo, concede 36 — e e' 36 que o campo +39 do 0x0070 tem de levar, nao 13.
    /// </summary>
    [Theory]
    [InlineData(13, 20, 20, 36)]
    [InlineData(11, 20, 20, 33)]
    [InlineData(12, 20, 20, 34)]
    [InlineData(50, 0, 0, 50)]      // sem gear, o cru e o final sao o mesmo numero
    public void OEcraLevaOValorConcedido(int cru, double pct, double fixo, int esperado) =>
        Assert.Equal(esperado, (int)Math.Round(cru * (1 + pct / 100.0) + fixo));

    /// <summary>
    /// O total do course soma os valores CONCEDIDOS e leva o bonus de conclusao por cima.
    /// Com as tres etapas do teste (33+34+36 = 103) e o "Feel So Good" a 70%, da' 175.
    /// </summary>
    [Fact]
    public void OTotalDoCourseSomaOsConcedidosEOBonus()
    {
        int total = 33 + 34 + 36;
        int bonus = (int)Math.Round(total * (70 / 100.0));
        Assert.Equal(103, total);
        Assert.Equal(72, bonus);
        Assert.Equal(175, total + bonus);
    }

    /// <summary>
    /// O campo do ecra e' u16: um total absurdo satura em vez de dar a volta. Sem o clamp,
    /// 70000 aparecia como 4464.
    /// </summary>
    [Fact]
    public void OCampoDoEcraSatura() =>
        Assert.Equal(ushort.MaxValue, (ushort)Math.Clamp(70000, 0, ushort.MaxValue));

    /// <summary>
    /// O bonus de XP da conclusao (campo `Exp` do CourseSection.ini) e' o irmao do de MAX.
    /// 29 dos 43 courses tem-no a ZERO — confirmado no jogo, o painel do "Let's Begin" mostra
    /// `经验值 0%` — e os outros 14 vao de 10% ate' 1500%.
    /// </summary>
    [Theory]
    [InlineData(0, 179, 0)]         // "Let's Begin": nao da' XP nenhum
    [InlineData(30, 179, 54)]       // "Dreams come true"
    [InlineData(1000, 179, 1790)]   // "Crush!"
    [InlineData(1500, 179, 2685)]   // "Shower Day"
    public void OBonusDeXpEUmaPercentagem(int pct, int xpDoCourse, int esperado) =>
        Assert.Equal(esperado, (int)Math.Round(xpDoCourse * (pct / 100.0)));

    /// <summary>
    /// O bonus de XP tem de passar pelo GanharXp e NAO pelo CompleteSong com maxGanho a zero:
    /// nesse caso o CompleteSong trata o zero como "sem leitura do cliente" e da' a media de
    /// 15 de MAX, ou seja acrescentava moeda que ninguem ganhou.
    /// </summary>
    [Fact]
    public void OBonusDeXpNaoMexeNoMax()
    {
        var p = new PlayerProfile(level: 12, xp: 0, max: 1000);
        p.GanharXp(500);

        Assert.Equal(1000, p.Max);
        Assert.Equal(500, p.Xp);
    }

    /// <summary>E se o bonus chegar para subir de nivel, sobe.</summary>
    [Fact]
    public void OBonusDeXpPodeSubirDeNivel()
    {
        var p = new PlayerProfile(level: 0, xp: 0, max: 0);   // nivel 1 do ecra, pede 40
        Assert.True(p.GanharXp(100));
        Assert.True(p.Level > 0);
    }
}
