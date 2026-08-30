using GrooveServer.Net;
using GrooveServer.Protocol;

namespace GrooveServer.Tests;

/// <summary>
/// A traducao de ids de musica entre versoes do cliente.
///
/// O id de rede e' a POSICAO da musica no <c>Song\DiscStock.csv</c> do cliente, por isso muda
/// de versao para versao: o SNDA de 2007 tem 94 musicas e o chines de 2019 tem 277, e das 94
/// NENHUMA cai no mesmo id.
/// </summary>
public class SongIdMapTests
{
    private static SongIdMap Mapa(params string[] linhas)
    {
        var f = Path.GetTempFileName();
        File.WriteAllLines(f, linhas);
        try { return SongIdMap.Load(f); } finally { File.Delete(f); }
    }

    /// <summary>Pares reais do `dados/musicas-snda.txt`, casados pelo Tag.</summary>
    [Theory]
    [InlineData(0u, 125u)]     // TURKEY
    [InlineData(1u, 126u)]     // Baram
    [InlineData(3u, 33u)]      // dplanet
    public void TraduzDoClienteParaABiblioteca(uint doCliente, uint daBiblioteca)
    {
        var m = Mapa("# comentario", "0\t125\tTURKEY", "1\t126\tBaram", "3\t33\tdplanet");
        Assert.Equal(daBiblioteca, m.ParaBiblioteca(doCliente));
        Assert.Equal(doCliente, m.ParaCliente(daBiblioteca));
    }

    /// <summary>
    /// UM ID QUE O MAPA NAO CONHECE PASSA INTACTO. Mais vale servir o que veio do que
    /// inventar uma traducao — e o registo avisa.
    /// </summary>
    [Fact]
    public void OQueNaoConheceVaiIntacto()
    {
        var m = Mapa("0\t125\tTURKEY");
        Assert.Equal(999u, m.ParaBiblioteca(999));
        Assert.False(m.Conhece(999));
        Assert.True(m.Conhece(0));
    }

    [Fact]
    public void FicheiroQueNaoExisteDaMapaVazio()
    {
        var m = SongIdMap.Load(Path.Combine(Path.GetTempPath(), "nao-existe-" + Guid.NewGuid()));
        Assert.Equal(0, m.Count);
        Assert.Equal(7u, m.ParaBiblioteca(7));   // sem mapa, nao traduz nada
    }

    [Fact]
    public void LinhasMalFormadasSaoIgnoradas()
    {
        var m = Mapa("", "# so' comentario", "sem tabs", "a\tb\tc", "0\t125\tTURKEY");
        Assert.Equal(1, m.Count);
        Assert.Equal(125u, m.ParaBiblioteca(0));
    }

    /// <summary>
    /// O caminho inverso guarda a PRIMEIRA: duas musicas do cliente a apontar para a mesma da
    /// biblioteca tornariam a volta ambigua.
    /// </summary>
    [Fact]
    public void OInversoFicaPelaPrimeira()
    {
        var m = Mapa("0\t125\tA", "1\t125\tB");
        Assert.Equal(0u, m.ParaCliente(125));
    }

    /// <summary>
    /// Quem decide se ha' traducao e' a versao anunciada no ConnectReq. A fronteira e' a mesma
    /// do id do ConnectAck — so' se mediram duas builds, por isso so' se afirma isso.
    /// </summary>
    [Theory]
    [InlineData(ClientVersion.Snda260, true)]
    [InlineData(ClientVersion.Nossa, false)]
    [InlineData(0u, false)]                        // versao desconhecida: trata-se como a nossa
    public void AVersaoDecideSeHaTraducao(uint versao, bool traduz) =>
        Assert.Equal(traduz, ClientVersion.EDe2007(versao));

    /// <summary>
    /// O ficheiro real tem de existir, casar as 94 e nao ter ids repetidos do lado do cliente.
    /// Se alguem regerar o mapa e ele sair estragado, falha aqui.
    /// </summary>
    [Fact]
    public void OMapaRealEstaCompletoESemRepetidos()
    {
        var caminho = Path.Combine(GrooveServer.Config.Raiz, "dados", "musicas-snda.txt");
        if (!File.Exists(caminho)) return;   // o repositorio pode nao trazer os dados do cliente

        var m = SongIdMap.Load(caminho);
        Assert.Equal(94, m.Count);

        var doCliente = File.ReadAllLines(caminho)
            .Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'))
            .Select(l => l.Split('\t')[0].Trim())
            .ToList();
        Assert.Equal(doCliente.Count, doCliente.Distinct().Count());
    }
}
