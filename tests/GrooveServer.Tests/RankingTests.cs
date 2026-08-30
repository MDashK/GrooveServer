using GrooveServer;
using GrooveServer.Net;
using GrooveServer.Protocol;

namespace GrooveServer.Tests;

/// <summary>
/// O modo RANKING: tres etapas somadas, e um recorde por canal.
///
/// VETORES: `gravacoes/ranking.txt` (corrida de 5K) e `ranking7k.txt` (duas corridas de 7K, a
/// primeira acabada em game over).
/// </summary>
public class RankingTests : IDisposable
{
    private readonly string _f = Path.Combine(Path.GetTempPath(), $"users-{Guid.NewGuid():N}.json");

    public void Dispose() { try { File.Delete(_f); } catch { } }

    private UserStore.Account Conta(string json)
    {
        File.WriteAllText(_f, json);
        return new UserStore(_f).Accounts[0];
    }

    /// <summary>
    /// Os quatro recordes sao independentes: dois modos x dois canais. Escrever num nao pode
    /// mexer em nenhum dos outros tres.
    /// </summary>
    [Fact]
    public void OsQuatroRecordesSaoIndependentes()
    {
        var c = Conta("""
        [ { "nome": "MDashK", "recorde": 259457, "recorde7k": 213082,
            "ranking": 695467, "ranking7k": 581128 } ]
        """);

        Assert.Equal(259457, c.RecordeDoCanal(Config.Canal5K));
        Assert.Equal(213082, c.RecordeDoCanal(Config.Canal7K));
        Assert.Equal(695467, c.RankingDoCanal(Config.Canal5K));
        Assert.Equal(581128, c.RankingDoCanal(Config.Canal7K));

        c.PorRankingDoCanal(Config.Canal7K, 700000);

        Assert.Equal(259457, c.BestScore);
        Assert.Equal(213082, c.BestScore7K);
        Assert.Equal(695467, c.RankingScore);
        Assert.Equal(700000, c.RankingScore7K);
    }

    /// <summary>
    /// OS QUATRO OFFSETS DO PAINEL DE PERFIL, medidos contra o ecra. Nao estao aos pares nem
    /// por ordem — o do ranking em 7K e' o +105 e nao o +121 que a simetria sugeria.
    /// </summary>
    [Fact]
    public void OPainelDePerfilTemQuatroMoradasDiferentes()
    {
        var corpo = new byte[144];
        BitConverter.TryWriteBytes(corpo.AsSpan(UserInfo.BestScoreOffset, 4), 259457u);
        BitConverter.TryWriteBytes(corpo.AsSpan(UserInfo.RankingScoreOffset, 4), 695467u);
        BitConverter.TryWriteBytes(corpo.AsSpan(UserInfo.RankingScore7KOffset, 4), 581128u);
        BitConverter.TryWriteBytes(corpo.AsSpan(UserInfo.BestScore7KOffset, 4), 213082u);

        Assert.Equal(new[] { 85, 89, 105, 117 },
                     new[] { UserInfo.BestScoreOffset, UserInfo.RankingScoreOffset,
                             UserInfo.RankingScore7KOffset, UserInfo.BestScore7KOffset });
        Assert.Equal(259457u, BitConverter.ToUInt32(corpo, 85));
        Assert.Equal(695467u, BitConverter.ToUInt32(corpo, 89));
        Assert.Equal(581128u, BitConverter.ToUInt32(corpo, 105));
        Assert.Equal(213082u, BitConverter.ToUInt32(corpo, 117));
    }

    /// <summary>
    /// A caixa do fim de musica tem TRES moradas — free mode, ranking 5K e ranking 7K — e o
    /// cliente le' a do canal em que esta'. Servir uma so' punha o recorde de 5K no ecra do 7K.
    /// </summary>
    [Fact]
    public void OFimDeMusicaTemUmCampoPorModoECanal()
    {
        var corpo = new byte[80];
        BitConverter.TryWriteBytes(corpo.AsSpan(UserProperty.RecordeOffset, 4), 259457u);
        BitConverter.TryWriteBytes(corpo.AsSpan(UserProperty.RecordeRankingOffset, 4), 695467u);
        BitConverter.TryWriteBytes(corpo.AsSpan(UserProperty.RecordeRanking7KOffset, 4), 581128u);

        Assert.Equal(new[] { 26, 30, 46 },
                     new[] { UserProperty.RecordeOffset, UserProperty.RecordeRankingOffset,
                             UserProperty.RecordeRanking7KOffset });
        Assert.Equal(259457u, BitConverter.ToUInt32(corpo, 26));
        Assert.Equal(695467u, BitConverter.ToUInt32(corpo, 30));
        Assert.Equal(581128u, BitConverter.ToUInt32(corpo, 46));
    }

    /// <summary>
    /// Nenhum dos campos do 0x0025 pisa outro: +26, +30 e +46 sao u32 e ha' espaco entre eles.
    /// </summary>
    [Fact]
    public void OsCamposDoFimDeMusicaNaoSeSobrepoem()
    {
        int[] offs = { UserProperty.RecordeOffset, UserProperty.RecordeRankingOffset,
                       UserProperty.MaxComboOffset, UserProperty.RecordeRanking7KOffset };
        foreach (var a in offs)
            foreach (var b in offs)
                if (a != b) Assert.True(a + 4 <= b || b + 4 <= a, $"+{a} e +{b} sobrepoem-se");
    }

    /// <summary>
    /// A corrida de 5K da captura: as tres etapas somam 695467, que e' o TOTAL do print, e bate
    /// o recorde de 678136. As parciais nao bastam — 234820 e 459710 ficam abaixo.
    /// </summary>
    [Fact]
    public void OTotalDaCorridaEASomaDasTresEtapas()
    {
        int[] etapas = { 234820, 224890, 235757 };

        Assert.Equal(695467, etapas.Sum());
        Assert.True(etapas[0] < 678136 && etapas[0] + etapas[1] < 678136);
        Assert.True(etapas.Sum() > 678136);
    }
}
