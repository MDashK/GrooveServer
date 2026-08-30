using GrooveServer.Protocol;

namespace GrooveServer.Tests;

/// <summary>
/// A versao que o cliente anuncia no ConnectReq, e o id com que se lhe responde.
///
/// Os vectores sao medidos, nao inventados: o ConnectReq vem tal e qual do primeiro C2S da
/// gravacao course_s1.txt, e os dois valores de versao saem do codigo dos dois clientes
/// (0x004318EF no exe da SNDA, DJMax.dll+0x430DC na nossa build). Ver Protocol.ClientVersion.
/// </summary>
public class ClientVersionTests
{
    /// O primeiro C2S da gravacao course_s1.txt, os 23 bytes do ConnectReq.
    private static byte[] ConnectReqReal => Convert.FromHexString(
        "0a007701020400000000000000000000ffffffff0e0000");

    [Fact]
    public void LeAVersaoDoConnectReqDaCapturaReal()
    {
        Assert.Equal(23, ConnectReqReal.Length);
        Assert.Equal(ClientVersion.Nossa, ClientVersion.Ler(ConnectReqReal));
    }

    [Fact]
    public void PacoteCurtoDaZeroEmVezDeRebentar()
    {
        Assert.Equal(0u, ClientVersion.Ler(new byte[] { 0x0A, 0x00, 0x77 }));
        Assert.Equal(0u, ClientVersion.Ler(Array.Empty<byte>()));
    }

    [Theory]
    [InlineData(ClientVersion.Nossa, ClientVersion.IdConnectAckPadrao)]   // v4.2 T1
    [InlineData(ClientVersion.Snda260, ClientVersion.IdConnectAckAntigo)] // SNDA 2.60
    [InlineData(0x00030000u, ClientVersion.IdConnectAckAntigo)]
    [InlineData(0x00050000u, ClientVersion.IdConnectAckPadrao)]
    [InlineData(0u, ClientVersion.IdConnectAckPadrao)]                    // desconhecida: padrao
    public void EscolheOIdDoConnectAckPelaVersaoMaior(uint versao, ushort esperado)
        => Assert.Equal(esperado, ClientVersion.IdDoConnectAck(versao));

    [Fact]
    public void OsDoisIdsSaoDiferentes()
        => Assert.NotEqual(ClientVersion.IdConnectAckPadrao, ClientVersion.IdConnectAckAntigo);

    [Theory]
    [InlineData(ClientVersion.Nossa, "v4.2 T1")]
    [InlineData(ClientVersion.Snda260, "v2.0 T6")]
    public void EscreveAVersaoComoOClienteAEscreveNoEcra(uint versao, string esperado)
        => Assert.Equal(esperado, ClientVersion.Nome(versao));

    [Fact]
    public void OConnectReqRealPedeOIdNovo()
        => Assert.Equal(ClientVersion.IdConnectAckPadrao,
                        ClientVersion.IdDoConnectAck(ClientVersion.Ler(ConnectReqReal)));
}
