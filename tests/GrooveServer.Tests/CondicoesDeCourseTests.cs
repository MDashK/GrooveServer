using GrooveServer.Net;
using GrooveServer.Protocol;

namespace GrooveServer.Tests;

/// <summary>
/// As condicoes de passagem de um course, do `[Clear]` do CourseSection.ini.
///
/// O servidor nao as conhecia de todo, e por isso um course jogado com 16,86% de precisao onde
/// se exigem 80% dava COURSE SUCCESS na mesma. Estes testes prendem a leitura e a comparacao.
///
/// **O QUE ELES NAO COBREM** e' o que falta: dizer o veredicto ao cliente. Nao ha' gravacao de
/// um course levado ate' ao fim sem cumprir as condicoes, portanto o campo que o carrega nunca
/// se viu mudar.
/// </summary>
public class CondicoesDeCourseTests : IDisposable
{
    private readonly string _f = Path.Combine(Path.GetTempPath(), $"courses-{Guid.NewGuid():N}.txt");

    public void Dispose() { try { File.Delete(_f); } catch { } }

    private CourseTable.Course Curso(params string[] extra)
    {
        File.WriteAllText(_f, "0\tteste\t10:2 20:2\t" + string.Join("\t", extra) + "\n");
        return CourseTable.Load(_f).Todos.First();
    }

    /// <summary>`80,1` e' "80 ou ACIMA"; `5,0` e' "5 ou ABAIXO". O sentido vem do .ini.</summary>
    [Theory]
    [InlineData("80,1", 80.0, true)]
    [InlineData("80,1", 79.99, false)]
    [InlineData("80,1", 100.0, true)]
    [InlineData("5,0", 5.0, true)]
    [InlineData("5,0", 6.0, false)]
    [InlineData("5,0", 0.0, true)]
    public void OSegundoNumeroEOSentidoDaComparacao(string texto, double medido, bool passa)
    {
        var c = CourseTable.Condicao.Ler(texto);

        Assert.NotNull(c);
        Assert.Equal(passa, c!.Value.Passa(medido));
    }

    /// <summary>`0,1` — "zero ou acima" — e' como o jogo desliga uma condicao. Passa sempre.</summary>
    [Fact]
    public void ZeroOuAcimaPassaSempre()
    {
        var c = CourseTable.Condicao.Ler("0,1")!.Value;

        Assert.True(c.Passa(0));
        Assert.True(c.Passa(999999));
    }

    [Theory]
    [InlineData("")]
    [InlineData("80")]
    [InlineData("80,2")]
    [InlineData("oitenta,1")]
    public void CondicaoIlegivelNaoSeInventa(string texto) =>
        Assert.Null(CourseTable.Condicao.Ler(texto));

    /// <summary>
    /// O "Feel So Good" tal como foi jogado no teste: pede 80% e combo 400, e o jogador fez
    /// 16,86% e 175. Falha as duas, e o servidor tem de o dizer.
    /// </summary>
    [Fact]
    public void OCourseDoTesteFalhaAsDuasCondicoes()
    {
        var c = Curso("precisao=80,1", "pontos=0,1", "breaks=0,1", "combo=400,1");

        var falhas = c.PorCumprir(precisao: 16.86, pontos: 147990, breaks: 26, combo: 175);

        Assert.Equal(2, falhas.Count);
        Assert.Contains(falhas, f => f.StartsWith("precisao"));
        Assert.Contains(falhas, f => f.StartsWith("combo"));
    }

    /// <summary>
    /// O "Enjoy! DJMAX!" do mesmo teste: passou a precisao (97,17 >= 95) e falhou o combo
    /// (566 < 1667). Uma condicao cumprida nao chega.
    /// </summary>
    [Fact]
    public void CumprirUmaCondicaoNaoChega()
    {
        var c = Curso("precisao=95,1", "pontos=0,1", "breaks=0,1", "combo=1667,1");

        var falhas = c.PorCumprir(precisao: 97.17, pontos: 1196873, breaks: 9, combo: 566);

        Assert.Single(falhas);
        Assert.Contains("combo", falhas[0]);
    }

    [Fact]
    public void CourseCumpridoNaoTemFalhas()
    {
        var c = Curso("precisao=95,1", "pontos=0,1", "breaks=0,1", "combo=400,1");

        Assert.Empty(c.PorCumprir(precisao: 99.0, pontos: 500000, breaks: 1, combo: 700));
    }

    /// <summary>Um course sem condicoes escritas nao falha nada — nao se inventam limites.</summary>
    [Fact]
    public void SemCondicoesNaoHaFalhas()
    {
        var c = Curso("preco=100");

        Assert.Empty(c.PorCumprir(precisao: 0, pontos: 0, breaks: 999, combo: 0));
    }

    /// <summary>
    /// AS MARCAS DO ECRA FINAL, contra a captura `curso_falhado.txt` posta ao lado do fim do
    /// `course3_s1` — o MESMO course ("Fine Day"), um falhado e outro passado.
    ///
    /// |  | +3 | +43 |
    /// |---|---|---|
    /// | passou | MaxPrice (50) | 3 |
    /// | falhou | 0xFF | 2 |
    ///
    /// O <c>+1</c> vale 1 nos dois: e' o que diz que a etapa e' a ultima do course, nao que
    /// ele foi passado. Confundir os dois era o que fazia um course falhado mostrar
    /// COURSE SUCCESS.
    /// </summary>
    [Fact]
    public void OEcraFinalDizSeOCoursePassou()
    {
        var passou = new byte[44];
        var falhou = new byte[44];

        StageResult.MarcarFimDeCourse(passou, 50, passou: true);
        StageResult.MarcarFimDeCourse(falhou, 50, passou: false);

        Assert.Equal(1, passou[StageResult.ScreenFimDeCourse]);
        Assert.Equal(50, passou[StageResult.ScreenPrecoCourse]);
        Assert.Equal(3, passou[StageResult.ScreenTipoFecho]);

        Assert.Equal(1, falhou[StageResult.ScreenFimDeCourse]);
        Assert.Equal(StageResult.PrecoDeCourseFalhado, falhou[StageResult.ScreenPrecoCourse]);
        Assert.Equal(2, falhou[StageResult.ScreenTipoFecho]);
    }

    /// <summary>
    /// O "Fine Day" da captura: 28,26% de precisao contra os 80% que ele exige. As outras tres
    /// condicoes estao a `0,1` e passam sempre — falha uma so', e uma chega.
    /// </summary>
    [Fact]
    public void OFineDayDaCapturaFalhaSoPelaPrecisao()
    {
        var c = Curso("precisao=80,1", "pontos=0,1", "breaks=0,1", "combo=0,1");

        var falhas = c.PorCumprir(precisao: 28.26, pontos: 217198, breaks: 16, combo: 325);

        Assert.Single(falhas);
        Assert.StartsWith("precisao", falhas[0]);
    }
}
