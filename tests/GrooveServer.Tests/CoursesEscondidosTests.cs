using GrooveServer.Net;
using GrooveServer.Protocol;

namespace GrooveServer.Tests;

/// <summary>
/// Um course que nao se pode servir NAO VAI PARA A LISTA DE ESCOLHA.
///
/// Quatro dos 48 precisam de uma musica que nao existe em canal nenhum — o `Crush!` pede a 177,
/// o `Enjoy! DJMAX!` a 189, o `Fire!` a 176 e o `NG` a 209, todas marcadas `offair` no catalogo
/// do cliente. Enquanto estavam trancados por creditos ninguem dava por isso; depois de abertos,
/// o jogador escolhia-os, o course arrancava, e ia abaixo ao chegar a' musica que falta.
///
/// **O `offair` nao os esconde sozinho:** no servidor original o `Enjoy! DJMAX!` aparece na
/// lista na mesma, apenas trancado por creditos. Quem decide o que a lista mostra e' o
/// <c>0x0082</c>, e mais nada.
/// </summary>
public class CoursesEscondidosTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), $"gs-{Guid.NewGuid():N}");

    public CoursesEscondidosTests() => Directory.CreateDirectory(_raiz);
    public void Dispose() { try { Directory.Delete(_raiz, true); } catch { } }

    /// <summary>
    /// Charts de mentira, mas no formato certo: cabecalho "SGL1" e uma entrada. A biblioteca
    /// valida a assinatura e deita fora o que nao a tenha — um ficheiro de zeros era ignorado
    /// em silencio e a pasta ficava vazia.
    /// </summary>
    private SongLibrary Biblioteca(string nome, params (uint Musica, byte Dif)[] charts)
    {
        string pasta = Path.Combine(_raiz, nome);
        Directory.CreateDirectory(pasta);
        foreach (var (musica, dif) in charts)
        {
            using var fs = File.Create(Path.Combine(pasta, $"song_{musica}_d{dif}.bin"));
            using var w = new BinaryWriter(fs);
            w.Write(0x314C4753u);          // "SGL1"
            w.Write(1);                    // uma entrada
            w.Write((ushort)0x007A);       // GameInfoInf
            w.Write(7); w.Write(new byte[7]);
            w.Write(140); w.Write(new byte[140]);
        }
        return new SongLibrary(pasta);
    }

    /// <summary>Um `courses.txt` a serio, para se exercitar o mesmo caminho do servidor.</summary>
    private CourseTable Tabela(params string[] linhas)
    {
        string f = Path.Combine(_raiz, $"courses-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(f, linhas);
        return CourseTable.Load(f);
    }

    [Fact]
    public void UmCourseComTodasAsMusicasEJogavel()
    {
        var lib = Biblioteca("a", (10, 2), (20, 2));
        var t = Tabela("0\tok\t10:2 20:2");

        Assert.True(CourseTable.Jogavel(t.Todos.First(), lib));
    }

    /// <summary>
    /// FALTAR A DIFICULDADE NAO CHEGA PARA ESCONDER. O recuo joga em NORMAL o que devia ser
    /// HARD, e e' assim que 44 dos 48 se jogam. Confundir as duas coisas escondia 31 courses
    /// que funcionam — que era, ao pe' da letra, o que os avisos do arranque contavam.
    /// </summary>
    [Fact]
    public void FaltarSoADificuldadeNaoEscondeOCourse()
    {
        var lib = Biblioteca("b", (10, 0), (20, 2));   // a musica 10 so' tem EASY
        var t = Tabela("0\trecuo\t10:2 20:2");         // e o course pede-a em HARD

        Assert.Empty(CourseTable.MusicasImpossiveis(t.Todos.First(), lib));
        Assert.True(CourseTable.Jogavel(t.Todos.First(), lib));
    }

    /// <summary>Nao haver chart NENHUM da musica e' que esconde.</summary>
    [Fact]
    public void MusicaSemChartNenhumEscondeOCourse()
    {
        var lib = Biblioteca("c", (10, 2));
        var t = Tabela("0\tCrush!\t10:2 177:2");

        Assert.Equal(new[] { 177u },
                     CourseTable.MusicasImpossiveis(t.Todos.First(), lib).Select(m => m.Song).ToArray());
        Assert.False(CourseTable.Jogavel(t.Todos.First(), lib));
    }

    [Fact]
    public void SoOsJogaveisEntramNaLista()
    {
        var lib = Biblioteca("d", (10, 2), (20, 2));
        var t = Tabela("0\tbom\t10:2",
                       "1\tCrush!\t10:2 177:2",
                       "2\ttambem bom\t20:2");

        Assert.Equal(new[] { 0, 2 }, t.Jogaveis(lib).Select(c => c.Indice).ToArray());
    }

    /// <summary>
    /// Sem biblioteca — o diagnostico `--sem-biblioteca` — nao se filtra nada. Nesse modo o
    /// servidor nem sequer constroi os blocos, e esconder courses so' confundiria a leitura.
    /// </summary>
    [Fact]
    public void SemBibliotecaNaoSeEscondeNada()
    {
        var t = Tabela("0\tCrush!\t177:2");

        Assert.True(CourseTable.Jogavel(t.Todos.First(), null));
        Assert.Single(t.Jogaveis(null));
    }

    /// <summary>
    /// O FILTRO E' POR CANAL, e tem de ser: as bibliotecas de 5K e 7K nao tem os mesmos charts,
    /// portanto o mesmo course pode ser jogavel de um lado e nao do outro.
    /// </summary>
    [Fact]
    public void CadaCanalEscondeOSeu()
    {
        var cincoK = Biblioteca("5k", (10, 2), (99, 2));
        var seteK = Biblioteca("7k", (10, 2));
        var t = Tabela("0\tso' a 10\t10:2",
                       "1\tprecisa da 99\t10:2 99:2");

        Assert.Equal(new[] { 0, 1 }, t.Jogaveis(cincoK).Select(c => c.Indice).ToArray());
        Assert.Equal(new[] { 0 }, t.Jogaveis(seteK).Select(c => c.Indice).ToArray());
    }

    /// <summary>
    /// O CLIENTE ANTIGO NAO CONHECE OS COURSES TODOS, e mandar-lhe um indice que ele nao tem
    /// no CourseSection.ini dele nao da' erro: CRASHA-O ao entrar no modo course. Medido — o
    /// `_VARIATIONS\DJMAX Online 01.18.2016` tem 43 courses onde o nosso tem 48.
    ///
    /// O limite corta a' cabeca e nao ha' buracos a meio: os 43 do cliente de 2016 sao
    /// exactamente os nossos 0..42. Ver Config.LimiteDeCourses.
    /// </summary>
    [Fact]
    public void OLimiteCortaOsCoursesQueOClienteNaoTem()
    {
        var lib = Biblioteca("limite", (10, 2), (20, 2), (30, 2));
        var t = Tabela("0	um	10:2", "1	dois	20:2", "2	tres	30:2");

        try
        {
            Config.LimiteDeCourses = 2;
            Assert.Equal(new[] { 0, 1 }, t.Jogaveis(lib).Select(c => c.Indice).ToArray());
        }
        finally { Config.LimiteDeCourses = 0; }
    }

    /// <summary>Limite 0 — o valor por omissao — nao corta nada.</summary>
    [Fact]
    public void SemLimiteVaoTodos()
    {
        var lib = Biblioteca("semlimite", (10, 2), (20, 2));
        var t = Tabela("0	um	10:2", "1	dois	20:2");

        Assert.Equal(0, Config.LimiteDeCourses);
        Assert.Equal(new[] { 0, 1 }, t.Jogaveis(lib).Select(c => c.Indice).ToArray());
    }

    /// <summary>
    /// O limite E' UM CORTE, nao um substituto do filtro por biblioteca: um course dentro do
    /// limite mas sem chart continua escondido. Somam-se, nao se anulam.
    /// </summary>
    [Fact]
    public void OLimiteSomaSeAoFiltroDaBiblioteca()
    {
        var lib = Biblioteca("ambos", (10, 2), (30, 2));
        var t = Tabela("0	um	10:2", "1	sem chart	177:2", "2	tres	30:2");

        try
        {
            Config.LimiteDeCourses = 2;
            Assert.Equal(new[] { 0 }, t.Jogaveis(lib).Select(c => c.Indice).ToArray());
        }
        finally { Config.LimiteDeCourses = 0; }
    }
}
