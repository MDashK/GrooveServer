using GrooveServer.Net;

namespace GrooveServer.Tests;

/// <summary>
/// A metade ALTA do id de catalogo, que decide se o cliente sabe desenhar o item.
///
/// O premio de um course era montado sempre com 2. Um avatar sorteado chegava como "seccao 2,
/// item 2065" — que nao existe — e a caixa do premio no ecra de COURSE SUCCESS ficava EM
/// BRANCO. Deu-se por isso porque o `Enjoy! DJMAX!` tem 2% de hipotese de sortear o
/// `Cosmic_Boy` e calhou.
///
/// VETOR: o inventario de uma conta a serio, cruzado com o `System\shop\ItemStock.csv`.
/// </summary>
public class CatalogoAltoTests : IDisposable
{
    private readonly string _f = Path.Combine(Path.GetTempPath(), $"itens-{Guid.NewGuid():N}.txt");

    public void Dispose() { try { File.Delete(_f); } catch { } }

    private ItemTable Tabela()
    {
        File.WriteAllLines(_f, new[]
        {
            "33880\tElle\texp=40\tmaxpc=20\thp=10\tdias=0\tuso=0\tseccao=2",
            "2065\tCosmic_Boy\texp=20\tmaxpc=10\thp=30\tdias=15\tuso=0\tseccao=2",
            "42020\tPG-P\texp=0\tmaxpc=0\thp=15\tdias=0\tuso=0\tseccao=4",
            "62465\tHP_Boost_LV1\texp=0\tmaxpc=0\thp=30\tdias=0\tuso=1\tseccao=5",
            "62721\tMAX_Boost_LV1\texp=0\tmaxpc=0\thp=0\tdias=0\tuso=1\tseccao=5",
            "99999\tSemSeccao\texp=0\tmaxpc=0\thp=0\tdias=0\tuso=1",
        });
        return ItemTable.Load(_f);
    }

    /// <summary>Os cinco catalogos medidos no inventario, reconstruidos a partir da seccao.</summary>
    [Theory]
    [InlineData(33880u, 99416u)]     // Elle, avatar
    [InlineData(42020u, 107556u)]    // PG-P, gear
    [InlineData(62465u, 193537u)]    // HP_Boost_LV1, consumivel
    [InlineData(62721u, 193793u)]    // MAX_Boost_LV1, consumivel
    public void OCatalogoBateComOQueEstaNoInventario(uint baixo, uint catalogo)
    {
        var t = Tabela();

        Assert.Equal(catalogo, (t.AltoDoCatalogo(baixo) << 16) | baixo);
    }

    /// <summary>O que estava errado: o Cosmic_Boy e' avatar, logo alto 1 e nao 2.</summary>
    [Fact]
    public void OCosmicBoyEAvatarEVaiComAltoUm()
    {
        var t = Tabela();

        Assert.Equal(ItemTable.AltoEquipavel, t.AltoDoCatalogo(2065));
        Assert.Equal(67601u, (t.AltoDoCatalogo(2065) << 16) | 2065);   // era 133137
    }

    /// <summary>Os consumiveis continuam como estavam — a maioria do que os courses dao.</summary>
    [Fact]
    public void OsConsumiveisMantemOAltoDois()
    {
        var t = Tabela();

        Assert.Equal(ItemTable.AltoConsumivel, t.AltoDoCatalogo(62465));
        Assert.Equal(ItemTable.AltoConsumivel, t.AltoDoCatalogo(62721));
    }

    /// <summary>
    /// Item que a tabela nao conhece — ha' dois assim nas lotarias, o 62566 e o 62950, que nem
    /// no ItemStock do jogo existem. Vai com o valor antigo, que serve os consumiveis.
    /// </summary>
    [Fact]
    public void ItemDesconhecidoVaiComOValorAntigo()
    {
        var t = Tabela();

        Assert.Equal(ItemTable.AltoConsumivel, t.AltoDoCatalogo(62566));
        Assert.Equal(ItemTable.AltoConsumivel, t.AltoDoCatalogo(99999));   // conhecido, mas sem seccao
    }
}
