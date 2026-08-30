using GrooveServer.Protocol;

namespace GrooveServer.Tests;

/// <summary>
/// O ecra COURSE SUCCESS: o Total Result e' do COURSE INTEIRO, nao da ultima musica.
///
/// VETORES: os <c>0x0070</c> por etapa das gravacoes `course_s1`, `course2_s1`, `course3_s1` e
/// `end_s1`, contra o servidor original. As etapas do meio levam <c>+43=2</c> e a ultima
/// <c>+43=3</c>; e' pondo umas ao lado das outras que se ve' o que cada coluna faz.
/// </summary>
public class TotalResultTests
{
    private static byte[] Ecra() => new byte[51];

    /// <summary>
    /// As tres gravacoes de course completo, cada uma com as etapas do meio e o total que a
    /// ultima anunciou. As notas da ultima etapa saem por diferenca, que e' como se mediram.
    /// </summary>
    public static TheoryData<string, int[], int[], int, int> Cursos() => new()
    {
        //                    notas por etapa      pontuacao por etapa           total notas  total pontos
        { "course_s1",  new[] { 122, 148, 132 }, new[] { 210980, 213183, 211880 }, 402, 636043 },
        { "course2_s1", new[] { 122, 146, 132 }, new[] { 203216, 208243, 210191 }, 400, 621650 },
        { "course3_s1", new[] { 262, 312, 402 }, new[] { 222718, 222065, 220943 }, 976, 665726 },
    };

    [Theory]
    [MemberData(nameof(Cursos))]
    public void AsNotasEAPontuacaoSaoASomaDasEtapas(string gravacao, int[] notas, int[] pontos,
                                                    int totalNotas, int totalPontos)
    {
        _ = gravacao;
        var ecra = Ecra();
        StageResult.MarcarTotaisDoCourse(ecra, notas.Sum(), pontos.Sum(), 0, 1f);

        Assert.Equal(totalNotas, notas.Sum());
        Assert.Equal(totalPontos, pontos.Sum());
        Assert.Equal(totalNotas, BitConverter.ToUInt16(ecra, StageResult.ScreenMax));
        Assert.Equal((uint)totalPontos, BitConverter.ToUInt32(ecra, StageResult.ScreenBaseScore));
    }

    /// <summary>
    /// O BONUS tambem soma: o "Fine Day" deu 20000 + 15000 + 5000 = 40000, e o da course_s1
    /// 40000 + 30000 + 40000 = 110000.
    /// </summary>
    [Theory]
    [InlineData(new[] { 40000, 30000, 40000 }, 110000)]
    [InlineData(new[] { 20000, 15000, 5000 }, 40000)]
    public void OBonusSoma(int[] porEtapa, int total)
    {
        var ecra = Ecra();
        StageResult.MarcarTotaisDoCourse(ecra, 0, 0, porEtapa.Sum(), 1f);

        Assert.Equal(total, porEtapa.Sum());
        Assert.Equal((uint)total, BitConverter.ToUInt32(ecra, StageResult.ScreenBonus));
    }

    /// <summary>
    /// A PRECISAO E' MEDIA SIMPLES. A `course_s1` separa os dois modelos sozinha: as duas
    /// primeiras etapas deram 100,0000 e 99,9324 e a terceira levou STEEL MAX, que exige
    /// 100,00. O valor gravado na ultima etapa e' 99,977478.
    ///
    /// Media simples: 99,97748.  Media pesada pelas notas (122, 148, 132): 99,9751.
    /// </summary>
    [Fact]
    public void APrecisaoEMediaSimplesDasEtapas()
    {
        const float Gravada = 99.977478f;   // o que o servidor real pôs em +31 da ultima etapa
        float[] etapas = { 100.0000f, 99.9324f, 100.0000f };
        int[] notas = { 122, 148, 132 };

        float simples = etapas.Sum() / etapas.Length;
        float pesada = (float)(etapas.Zip(notas, (p, n) => p * n).Sum() / notas.Sum());

        // A media simples bate; a pesada fica a 0,0024 de distancia, que a esta escala e' muito
        // mais do que o erro de eu partir de valores ja' arredondados a quatro casas.
        Assert.True(Math.Abs(simples - Gravada) < 0.0005f,
                    $"media simples {simples} contra o gravado {Gravada}");
        Assert.True(Math.Abs(pesada - Gravada) > 0.002f,
                    $"media pesada {pesada} contra o gravado {Gravada}");

        var ecra = Ecra();
        StageResult.MarcarTotaisDoCourse(ecra, 0, 0, 0, simples);
        Assert.Equal(simples, BitConverter.ToSingle(ecra, StageResult.ScreenAccuracy));
    }

    /// <summary>
    /// O DISCO E O BONUS DE CADA ETAPA, contra as 17 etapas reais das quatro gravacoes. Duas
    /// destas linhas contradiziam a tabela antiga: a de 99,58 (que levou BRONZE MAX e nao
    /// SILVER) e as quatro de GOLDEN DISC sem breaks (15000 e nao 5000).
    /// </summary>
    [Theory]
    [InlineData(100.00f, 0, 1, 40000)]    // STEEL MAX
    [InlineData(99.93f, 0, 6, 30000)]     // GOLDEN MAX
    [InlineData(99.58f, 0, 8, 20000)]     // BRONZE MAX — a antiga dava SILVER/25000
    [InlineData(99.44f, 0, 8, 20000)]
    [InlineData(99.00f, 0, 8, 20000)]
    [InlineData(98.79f, 0, 8, 20000)]
    [InlineData(98.31f, 0, 9, 15000)]     // GOLDEN DISC com all combo — a antiga dava 5000
    [InlineData(97.08f, 0, 9, 15000)]
    [InlineData(96.76f, 0, 9, 15000)]
    [InlineData(96.16f, 0, 9, 15000)]     // a antiga dava SILVER DISC
    [InlineData(97.57f, 2, 9, 5000)]      // GOLDEN DISC sem all combo
    [InlineData(97.40f, 1, 9, 5000)]
    [InlineData(0.04f, 44, 17, 0)]        // game over
    public void ODiscoEOBonusBatemComAsEtapasReais(float precisao, int breaks,
                                                   int codigo, int bonus)
    {
        var (c, b) = ScoreFormula.Disco(precisao, breaks);

        Assert.Equal((ushort)codigo, c);
        Assert.Equal(bonus, b);
    }

    /// <summary>
    /// A familia MAX ja' exige combo perfeito, por isso NAO leva o extra de all combo por
    /// cima — se levasse, o STEEL MAX daria 50000 onde a gravacao mostra 40000.
    /// </summary>
    [Fact]
    public void AFamiliaMaxNaoAcumulaOExtraDoAllCombo()
    {
        Assert.Equal(40000, ScoreFormula.Disco(100.00f, 0).Bonus);
        Assert.Equal(20000, ScoreFormula.Disco(98.40f, 0).Bonus);

        // e logo abaixo da fronteira o extra aparece
        Assert.Equal(5000 + ScoreFormula.BonusAllCombo, ScoreFormula.Disco(98.39f, 0).Bonus);
        Assert.Equal(5000, ScoreFormula.Disco(98.39f, 1).Bonus);
    }

    /// <summary>
    /// A pontuacao base de cada etapa, contra as doze etapas concluidas. E' lei exacta quando
    /// nao ha' breaks (erro maximo medido: 0,37%) e ajuste quando ha'.
    /// </summary>
    [Theory]
    [InlineData(122, 100.00f, 122, 0, 210980)]
    [InlineData(148, 99.93f, 148, 0, 213183)]
    [InlineData(262, 99.58f, 262, 0, 222718)]
    [InlineData(237, 98.31f, 237, 0, 217874)]
    [InlineData(321, 99.44f, 321, 0, 227733)]
    [InlineData(403, 97.40f, 326, 1, 225471)]
    public void APontuacaoBaseFicaAMenosDeUmPorCentoDoReal(int notas, float precisao,
                                                           int combo, int breaks, int real)
    {
        int previsto = ScoreFormula.Base(notas, precisao, combo, breaks);

        Assert.InRange(100.0 * Math.Abs(previsto - real) / real, 0, 1.0);
    }
}
