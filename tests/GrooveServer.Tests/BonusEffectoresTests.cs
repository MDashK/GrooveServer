using GrooveServer.Protocol;

namespace GrooveServer.Tests;

/// <summary>
/// O bonus dos effectores, que soma ao 奖励得分 por cima do disco e do all combo.
///
/// VETOR: `gravacoes/efeito_bonus.txt` — quatro corridas da mesma musica (`風にお願い`, 5K),
/// todas com all combo, so' a mudar os effectores. A caixa do ecra escreve as parcelas em texto
/// e o `+27` da' o total, portanto o valor fica preso pelos dois lados.
/// </summary>
public class BonusEffectoresTests
{
    private static byte[] Efe(params (int Offset, byte Valor)[] campos)
    {
        var b = new byte[Effectores.Tamanho];
        foreach (var (o, v) in campos) b[o] = v;
        return b;
    }

    /// <summary>
    /// A FAMILIA DOS FADERS, agora completa e toda medida no ecra. Estes numeros derrubaram a
    /// regra "1000 x codigo" que aqui estava: ela previa 1000 para o BLINK, que vale 4000.
    /// </summary>
    [Theory]
    [InlineData(1, 4000)]    // FADER BLINK — a que derrubou a regra
    [InlineData(2, 3000)]    // FADER IN
    [InlineData(3, 3000)]    // FADER OUT
    [InlineData(4, 4000)]    // FOG
    public void CadaFaderValeOQueOEcraMostrou(byte codigo, int bonus) =>
        Assert.Equal(bonus, ScoreFormula.BonusDosEffectores(Efe((Effectores.FadersOffset, codigo))));

    /// <summary>A familia do arranjo, tambem completa.</summary>
    [Theory]
    [InlineData(1, 2000)]    // 5K MIRROR — a regra antiga previa 1000
    [InlineData(2, 2000)]    // 5K R-SHIFT
    [InlineData(3, 3000)]    // 5K RANDOM
    public void OArranjoTambem(byte codigo, int bonus) =>
        Assert.Equal(bonus, ScoreFormula.BonusDosEffectores(Efe((Effectores.ArranjoOffset, codigo))));

    /// <summary>
    /// Um codigo fora da tabela vale ZERO, nao um valor inventado. Era o risco de manter uma
    /// formula: qualquer byte que aparecesse naquela casa virava milhares de pontos.
    /// </summary>
    [Fact]
    public void UmCodigoDesconhecidoNaoPagaNada() =>
        Assert.Equal(0, ScoreFormula.BonusDosEffectores(Efe((Effectores.FadersOffset, 9))));

    /// <summary>
    /// O ECRA TEM DE MOSTRAR O MESMO QUE A CONTA DO RECORDE SOMA.
    ///
    /// Este e' o caso que apareceu no jogo: SPEED BAT com FADER OUT e 5K RANDOM anunciava
    /// **6000** em vez de 10000. Os 6000 sao os dois effectores; faltava a caixa SPEED, porque
    /// eu tinha somado o bonus dela na conta do recorde e nao no <c>Apply</c> que escreve o
    /// ecra. Duas contas com parcelas diferentes para a mesma jogada.
    /// </summary>
    [Fact]
    public void OEcraSomaAVelocidadeTalComoOsEffectores()
    {
        var ecra = new byte[44];
        var relatorio = new byte[52];
        BitConverter.TryWriteBytes(relatorio.AsSpan(StageResult.ReportTotalNotes, 2), (ushort)300);

        StageResult.Apply(ecra, relatorio, breaks: 0, precisao: 99.0f, xpGanho: 30,
                          effectores: Efe((Effectores.FadersOffset, 3), (Effectores.ArranjoOffset, 3)),
                          modoVelocidade: ScoreFormula.VelocidadeBat);

        // 99,0% sem breaks e' BRONZE MAX, que ja' traz o combo perfeito embutido.
        var (_, doDisco) = ScoreFormula.Disco(99.0f, 0);
        int noEcra = (int)BitConverter.ToUInt32(ecra, StageResult.ScreenBonus);

        Assert.Equal(doDisco + 3000 + 3000 + 4000, noEcra);
        Assert.Equal(10000, noEcra - doDisco);
    }

    /// <summary>
    /// A CAIXA SPEED, as quatro medidas. Vem do indice no cabecalho do 0x00C3, nao do corpo:
    /// o OFF e o SPEED UP tem o corpo byte a byte igual e pagam 0 e 2000.
    ///
    /// Medido com o 微风祈愿 em EZ, so' a mudar a caixa, e confirmado de fora pela highscore_s1.
    /// </summary>
    [Theory]
    [InlineData(ScoreFormula.VelocidadeDown, 3000)]
    [InlineData(ScoreFormula.VelocidadeUp, 2000)]
    [InlineData(ScoreFormula.VelocidadeChaosX, 5000)]
    [InlineData(ScoreFormula.VelocidadeBat, 4000)]
    public void CadaModoDeVelocidadePagaOQueOEcraMostrou(int indice, int bonus) =>
        Assert.Equal(bonus, ScoreFormula.BonusDaVelocidade(indice));

    /// <summary>
    /// A caixa desligada e os multiplicadores simples nao pagam nada. O indice 2 e' o "x2" das
    /// sete corridas da highscore_s1, cujo bonus fica todo explicado pelo disco e pelos
    /// effectores — nao sobra nada para a velocidade.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(99)]
    public void ForaDaTabelaNaoPagaNada(int indice) =>
        Assert.Equal(0, ScoreFormula.BonusDaVelocidade(indice));

    /// <summary>
    /// SOMAM-SE. A quarta corrida tinha FOG e 5K RANDOM e a caixa deu 7000, que e' 4000 + 3000
    /// exactos. Sem isto ficava-se sem saber se contava so' um.
    /// </summary>
    [Fact]
    public void OsEffectoresSomamEntreSi() =>
        Assert.Equal(7000, ScoreFormula.BonusDosEffectores(
            Efe((Effectores.FadersOffset, 4), (Effectores.ArranjoOffset, 3))));

    [Fact]
    public void SemEffectoresNaoHaBonus()
    {
        Assert.Equal(0, ScoreFormula.BonusDosEffectores(null));
        Assert.Equal(0, ScoreFormula.BonusDosEffectores(Efe()));
    }

    /// <summary>
    /// SO' AS DUAS CASAS CONHECIDAS CONTAM. Numa captura o +0 valeu 11 sem se saber porque';
    /// somar os 20 bytes todos inventava 11000 do nada.
    /// </summary>
    [Fact]
    public void OsBytesPorIdentificarNaoContam() =>
        Assert.Equal(0, ScoreFormula.BonusDosEffectores(Efe((0, 11))));

    /// <summary>
    /// Os totais das quatro corridas, reconstruidos: disco + all combo + effectores. Sao os
    /// numeros que o `+27` levou.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 20000)]    // sem effector, BRONZE MAX com all combo
    [InlineData(4, 0, 19000)]    // FOG, GOLDEN DISC com all combo (15000 + 4000)
    [InlineData(0, 3, 23000)]    // 5K RANDOM, BRONZE MAX (20000 + 3000)
    [InlineData(4, 3, 22000)]    // os dois, GOLDEN DISC (15000 + 7000)
    public void OsQuatroTotaisDaCaptura(byte fader, byte arranjo, int esperado)
    {
        // o disco daquela corrida: BRONZE MAX quando nao houve FOG, GOLDEN DISC quando houve
        float precisao = fader == 0 ? 99.0f : 98.0f;
        int disco = ScoreFormula.Disco(precisao, 0).Bonus;

        Assert.Equal(esperado, disco + ScoreFormula.BonusDosEffectores(Efe((2, fader), (8, arranjo))));
    }
}
