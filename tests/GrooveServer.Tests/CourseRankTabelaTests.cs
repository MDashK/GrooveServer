using GrooveServer.Net;
using GrooveServer.Protocol;

namespace GrooveServer.Tests;

/// <summary>
/// A tabela de high scores de um course: COMUM A TODAS AS CONTAS e SEPARADA POR CANAL.
///
/// As duas coisas estavam mal ao mesmo tempo, e cada uma escondia metade da outra:
///
/// * a tabela era construida so' com a conta ligada, portanto o recorde do MDashK nunca
///   aparecia ao MDK — a tabela dele saia vazia;
/// * a chave era o numero do course, sem canal, portanto o mesmo course em 5K e em 7K
///   disputava o mesmo lugar, com charts que nao se comparam.
/// </summary>
public class CourseRankTabelaTests : IDisposable
{
    private readonly string _ficheiro = Path.Combine(Path.GetTempPath(),
                                                     $"users-{Guid.NewGuid():N}.json");

    public void Dispose() { try { File.Delete(_ficheiro); } catch { } }

    private UserStore Store(string json)
    {
        File.WriteAllText(_ficheiro, json);
        return new UserStore(_ficheiro);
    }

    private const string TresContas = """
    [
      { "nome": "MDashK", "courses": { "5k:0": "746043,402,20260807", "7k:0": "310000,120,20260810" } },
      { "nome": "MDK",    "courses": { "5k:0": "618502,280,20260718" } },
      { "nome": "Zeca",   "courses": { "7k:0": "500000,200,20260811" } }
    ]
    """;

    [Fact]
    public void ATabelaTrazTodasAsContasENaoSoAQueEstaLigada()
    {
        var store = Store(TresContas);

        var tabela = store.TabelaDoCourse("5k:0", euNome: "MDK", euId: 360, lugares: 50);

        Assert.Equal(2, tabela.Count);
        Assert.Contains(tabela, e => e.Nome == "MDashK");
        Assert.Contains(tabela, e => e.Nome == "MDK");
    }

    /// <summary>Do melhor para o pior — quem ve' a tabela nao ganha lugar por a estar a ver.</summary>
    [Fact]
    public void OsLugaresVaoPorPontuacaoDecrescente()
    {
        var store = Store(TresContas);

        var tabela = store.TabelaDoCourse("5k:0", euNome: "MDK", euId: 360, lugares: 50);

        Assert.Equal("MDashK", tabela[0].Nome);
        Assert.Equal(746043, tabela[0].Score);
        Assert.Equal("MDK", tabela[1].Nome);
    }

    /// <summary>
    /// O CANAL SEPARA AS TABELAS. O mesmo course 0 tem gente diferente e pontuacoes diferentes
    /// nos dois lados, e nenhuma entrada de um aparece no outro.
    /// </summary>
    [Fact]
    public void CadaCanalTemASuaTabela()
    {
        var store = Store(TresContas);

        var cinco = store.TabelaDoCourse("5k:0", "MDashK", 360, 50);
        var sete = store.TabelaDoCourse("7k:0", "MDashK", 360, 50);

        Assert.Equal(new[] { "MDashK", "MDK" }, cinco.Select(e => e.Nome).ToArray());
        Assert.Equal(new[] { "Zeca", "MDashK" }, sete.Select(e => e.Nome).ToArray());
        Assert.Equal(746043, cinco.First(e => e.Nome == "MDashK").Score);
        Assert.Equal(310000, sete.First(e => e.Nome == "MDashK").Score);
    }

    /// <summary>
    /// Os ids nao se podem repetir: o corpo do 0x0084 e' uma cadeia em que cada lugar aponta
    /// para o seguinte. E o de quem esta' ligado tem de ser o VERDADEIRO, o que o login lhe deu.
    /// </summary>
    [Fact]
    public void OsIdsSaoTodosDiferentesEOMeuEODoLogin()
    {
        var store = Store(TresContas);

        var tabela = store.TabelaDoCourse("5k:0", euNome: "MDK", euId: 360, lugares: 50);

        Assert.Equal(tabela.Count, tabela.Select(e => e.Id).Distinct().Count());
        Assert.Equal(360, tabela.First(e => e.Nome == "MDK").Id);
        Assert.True(tabela.First(e => e.Nome == "MDashK").Id >= UserStore.IdSinteticoBase);
    }

    /// <summary>Antes do login nao ha' conta ligada, e a tabela continua a sair.</summary>
    [Fact]
    public void SemContaLigadaATabelaSaiNaMesma()
    {
        var store = Store(TresContas);

        var tabela = store.TabelaDoCourse("5k:0", euNome: null, euId: 0, lugares: 50);

        Assert.Equal(2, tabela.Count);
        Assert.DoesNotContain(tabela, e => e.Id == 0);
    }

    [Fact]
    public void CourseQueNinguemJogouDaTabelaVazia()
    {
        var store = Store(TresContas);

        Assert.Empty(store.TabelaDoCourse("5k:47", "MDashK", 360, 50));
    }

    /// <summary>
    /// A MIGRACAO das contas escritas antes de haver canal: a chave crua passa a 5K, e o valor
    /// segue intacto. Sem isto os recordes que ja' existiam desapareciam das tabelas.
    /// </summary>
    [Fact]
    public void AsPontuacoesSemCanalPassamParaCincoK()
    {
        var store = Store("""
        [ { "nome": "MDashK", "courses": { "0": "746043,402,20260807", "24": "310000,120,20260810" } } ]
        """);

        var conta = store.Accounts[0];
        Assert.Equal(new[] { "5k:0", "5k:24" }, conta.CourseScores.Keys.OrderBy(k => k).ToArray());
        Assert.Equal("746043,402,20260807", conta.CourseScores["5k:0"]);
    }

    /// <summary>Se ja' houver a chave com canal, e' essa que fica — e' a mais recente.</summary>
    [Fact]
    public void AChaveComCanalGanhaSobreACrua()
    {
        var store = Store("""
        [ { "nome": "MDashK", "courses": { "0": "1,1,20260101", "5k:0": "999,9,20260808" } } ]
        """);

        var conta = store.Accounts[0];
        Assert.Single(conta.CourseScores);
        Assert.Equal("999,9,20260808", conta.CourseScores["5k:0"]);
    }

    /// <summary>
    /// A tabela cabe no corpo real: 50 lugares em 2148 bytes, e o que passa disso e' cortado
    /// pelo fim (que e' o pior, nao o melhor).
    /// </summary>
    [Fact]
    public void NaoSePassaDosLugaresQueCabem()
    {
        var contas = string.Join(",", Enumerable.Range(0, 60)
            .Select(i => $$"""{ "nome": "J{{i}}", "courses": { "5k:0": "{{1000 + i}},1,20260101" } }"""));
        var store = Store($"[{contas}]");

        int lugares = CourseRank.Lugares(2148);
        var tabela = store.TabelaDoCourse("5k:0", "J0", 360, lugares);

        Assert.Equal(50, lugares);
        Assert.Equal(50, tabela.Count);
        Assert.Equal(1059, tabela[0].Score);     // o melhor entrou
        Assert.DoesNotContain(tabela, e => e.Nome == "J0");   // o pior ficou de fora
    }
}
