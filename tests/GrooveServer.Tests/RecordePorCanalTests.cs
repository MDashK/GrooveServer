using GrooveServer;
using GrooveServer.Net;
using GrooveServer.Protocol;

namespace GrooveServer.Tests;

/// <summary>
/// O recorde de free mode e' POR CANAL, e o painel de perfil mostra os dois ao mesmo tempo:
/// `自由模式最高得分` com uma caixa `LIGHT CHANNEL 5KEYS` e outra `MANIA CHANNEL 7KEYS`.
///
/// Guardava-se um numero so' e servia-se sempre em +85, portanto a caixa de 7KEYS ficava
/// eternamente a zero e uma jogada de 7K podia escrever por cima do recorde de 5K.
/// </summary>
public class RecordePorCanalTests : IDisposable
{
    private readonly string _ficheiro = Path.Combine(Path.GetTempPath(),
                                                     $"users-{Guid.NewGuid():N}.json");

    public void Dispose() { try { File.Delete(_ficheiro); } catch { } }

    private UserStore.Account Conta()
    {
        File.WriteAllText(_ficheiro,
            """[ { "nome": "MDashK", "recorde": 259457, "recorde7k": 212570 } ]""");
        return new UserStore(_ficheiro).Accounts[0];
    }

    [Fact]
    public void CadaCanalLeOSeuRecorde()
    {
        var c = Conta();

        Assert.Equal(259457, c.RecordeDoCanal(Config.Canal5K));
        Assert.Equal(212570, c.RecordeDoCanal(Config.Canal7K));
    }

    /// <summary>Escrever num canal nao pode mexer no outro — era o que acontecia.</summary>
    [Fact]
    public void EscreverNumCanalNaoTocaNoOutro()
    {
        var c = Conta();

        c.PorRecordeDoCanal(Config.Canal7K, 300000);

        Assert.Equal(259457, c.BestScore);
        Assert.Equal(300000, c.BestScore7K);

        c.PorRecordeDoCanal(Config.Canal5K, 400000);

        Assert.Equal(400000, c.BestScore);
        Assert.Equal(300000, c.BestScore7K);
    }

    /// <summary>
    /// Os dois campos do <c>0x0043</c>, contra o corpo real da `del_s1` — a unica gravacao
    /// tirada depois de o jogador ter acabado uma musica de 7K. Nela o +85 vale 259457 e o
    /// +117 vale 212570; nas outras sete o +117 esta' a zero.
    /// </summary>
    [Fact]
    public void OsDoisRecordesVaoEmOffsetsDiferentes()
    {
        var corpo = new byte[138];

        BitConverter.TryWriteBytes(corpo.AsSpan(UserInfo.BestScoreOffset, 4), 259457u);
        BitConverter.TryWriteBytes(corpo.AsSpan(UserInfo.BestScore7KOffset, 4), 212570u);

        Assert.Equal(85, UserInfo.BestScoreOffset);
        Assert.Equal(117, UserInfo.BestScore7KOffset);
        Assert.Equal(259457u, BitConverter.ToUInt32(corpo, UserInfo.BestScoreOffset));
        Assert.Equal(212570u, BitConverter.ToUInt32(corpo, UserInfo.BestScore7KOffset));
    }

    /// <summary>
    /// Os dois campos nao se sobrepoem nem pisam os vizinhos ja' medidos — o combo maximo
    /// (+93), a melhor precisao (+97) e a precisao media (+101).
    /// </summary>
    [Fact]
    public void NenhumDosDoisPisaOsCamposVizinhos()
    {
        var ocupados = new[]
        {
            (UserInfo.BestScoreOffset, 4), (UserInfo.BestScore7KOffset, 4),
            (UserInfo.MaxComboOffset, 4), (UserInfo.BestAccuracyOffset, 4),
            (UserInfo.AvgAccuracyOffset, 2),
        };

        foreach (var (a, ta) in ocupados)
            foreach (var (b, tb) in ocupados)
                if (a != b)
                    Assert.True(a + ta <= b || b + tb <= a, $"+{a} e +{b} sobrepoem-se");
    }
}
