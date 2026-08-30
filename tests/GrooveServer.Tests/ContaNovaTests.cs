using GrooveServer.Net;

namespace GrooveServer.Tests;

/// <summary>
/// Como comeca uma conta nova, medido contra o servidor original.
///
/// VETOR: `gravacoes/conta_nova_s1.txt` — uma conta criada de raiz. O `0x0043` do primeiro
/// login diz nivel 1 (interno), 0 de experiencia e 0x4E20 = 20000 de MAX; o `0x0044` traz a
/// coleccao vazia e UM item por equipar.
///
/// Nada disto era o que se fazia: a conta nascia a nivel 0, sem MAX e sem nada.
/// </summary>
public class ContaNovaTests : IDisposable
{
    private readonly string _f = Path.Combine(Path.GetTempPath(), $"users-{Guid.NewGuid():N}.json");

    public void Dispose() { try { File.Delete(_f); } catch { } }

    private UserStore.Account Nova() => new UserStore(_f).GetOrCreate("Axia01");

    [Fact]
    public void NasceNoNivelUmParaOTutorialAparecer()
    {
        var c = Nova();

        Assert.Equal(0, c.Level);        // interno; o ecra soma um. Divergencia escolhida: ver UserStore
        Assert.Equal(0, c.Xp);
    }

    /// <summary>20000 MAX, que e' o que o painel de perfil da conta nova mostrava.</summary>
    [Fact]
    public void NasceComVinteMilDeMax() => Assert.Equal(20000, Nova().Max);

    /// <summary>
    /// UM item de oferta, por equipar: o `炫紫MK2` / `Violet_MK2`. A metade alta do catalogo e'
    /// 1 porque e' um equipavel da seccao 4 — ver Net.ItemTable.AltoDoCatalogo.
    /// </summary>
    [Fact]
    public void NasceComOItemDeBoasVindas()
    {
        var c = Nova();

        var item = Assert.Single(c.Items);
        Assert.Equal(107521u, item.CatalogId);
        Assert.Equal(41985u, item.CatalogId & 0xFFFF);
        Assert.Equal(1u, item.CatalogId >> 16);
        Assert.False(item.Equipped);
        Assert.NotEqual(0u, item.InstanceId);
    }

    /// <summary>A coleccao comeca vazia — o ecra mostrava os quadrados todos por descobrir.</summary>
    [Fact]
    public void ComecaSemColeccaoESemRecordes()
    {
        var c = Nova();

        Assert.Empty(c.DefaultItems);
        Assert.Empty(c.CourseScores);
        Assert.Equal(0, c.BestScore);
        Assert.Equal(0, c.RankingScore);
    }

    /// <summary>Criar duas vezes o mesmo nome nao duplica nem volta a dar o item.</summary>
    [Fact]
    public void CriarDuasVezesDaAMesmaConta()
    {
        var store = new UserStore(_f);
        var a = store.GetOrCreate("Axia01");
        a.Max = 999;
        var b = store.GetOrCreate("Axia01");

        Assert.Same(a, b);
        Assert.Equal(999, b.Max);
        Assert.Single(b.Items);
    }
}
