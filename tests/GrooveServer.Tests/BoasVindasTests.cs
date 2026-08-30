using GrooveServer.Net;
using GrooveServer.Protocol;

namespace GrooveServer.Tests;

/// <summary>
/// O ecra de boas-vindas de uma conta nova: nickname, idade e sexo.
///
/// A conta e' criada no SITE do jogo, com utilizador e password; **o resto define-se no jogo** e
/// passa mesmo pela rede, em duas mensagens que o proprio cliente nomeia — `UpdUserAccountNickAck`
/// e `UpdUserProfileAck`.
///
/// VETOR: `gravacoes/conta_nova_s0.txt`, 52 segundos depois do login — o tempo de escrever na
/// caixa. Os campos vao no cabecalho, que viaja em claro:
///
///     0x0030   30 00 | 0a | 41 78 69 61     -> "Axia"
///     0x0032   32 00 | 44 | 00 16 00 01     -> 22 anos, sexo 1
/// </summary>
public class BoasVindasTests
{
    private static byte[] Pacote(params byte[] apos) =>
        new byte[] { 0x30, 0x00, 0x0a }.Concat(apos).ToArray();

    /// <summary>O nickname da captura, que cabe todo no cabecalho.</summary>
    [Fact]
    public void ONicknameLeSeDoCabecalho() =>
        Assert.Equal("Axia", BoasVindas.LerNickname(
            Pacote(0x41, 0x78, 0x69, 0x61), Array.Empty<byte>()));

    /// <summary>Um nome maior que quatro letras continua pelo corpo.</summary>
    [Fact]
    public void UmNomeMaiorContinuaNoCorpo() =>
        Assert.Equal("AxiaLonga", BoasVindas.LerNickname(
            Pacote(0x41, 0x78, 0x69, 0x61),
            new byte[] { 0x4c, 0x6f, 0x6e, 0x67, 0x61, 0x00, 0x00, 0x00 }));

    /// <summary>
    /// A unica amostra que ha' do 0x0032, tirada da gravacao do servidor online: a Axia,
    /// feminina, 22 anos. 32 00 44 | 00 16 00 01.
    ///
    /// **Esta amostra nao distingue os dois candidatos a campo do sexo**, e foi por isso que eu
    /// escolhi mal a primeira vez: o +6 vale 1 e o +3 vale 0, e com a conversao 1-baseada as
    /// duas leituras dao "feminino" aqui. O que decidiu foi terem sido criadas contas novas,
    /// uma delas masculina: o +6 deu 1 nas tres, logo e' constante. Fica o +3.
    ///
    /// Enquanto nao houver uma amostra masculina ao byte, o registo despeja o cabecalho em
    /// bruto (ver <see cref="BoasVindas.Cru"/>) para se poder ver o valor sem outra captura.
    /// </summary>
    [Fact]
    public void OPerfilLeSeDoCabecalho()
    {
        var p = new byte[] { 0x32, 0x00, 0x44, 0x00, 0x16, 0x00, 0x01, 0x00 };

        var (idade, sexo) = BoasVindas.LerPerfil(p);

        Assert.Equal(22, idade);
        Assert.Equal(1, sexo);              // feminino
        Assert.Equal(0, (byte)(sexo - 1));  // e' isto que vai para o painel, no 0x0043 +50
    }

    /// <summary>
    /// O sexo esta' no +3, nao no +6. Uma amostra masculina hipotetica, igual a' real excepto
    /// nesse byte, tem de dar masculino — e' este teste que fecha a porta ao engano anterior.
    /// </summary>
    [Fact]
    public void OSexoSaiDoTerceiroByteENaoDoSexto()
    {
        var masculino = new byte[] { 0x32, 0x00, 0x44, 0x01, 0x16, 0x00, 0x01, 0x00 };

        var (_, sexo) = BoasVindas.LerPerfil(masculino);

        Assert.Equal(2, sexo);
    }

    /// <summary>Chega ler ate' ao +5; o cabecalho tem sete bytes, mas a idade acaba no quinto.</summary>
    [Fact]
    public void PacoteCurtoNaoSeLe() =>
        Assert.False(BoasVindas.PodeLerPerfil(new byte[] { 0x32, 0x00, 0x44, 0x00, 0x16 }));

    /// <summary>
    /// O tipo do 0x0039. Os dois primeiros bytes do corpo saem das gravacoes tal e qual:
    /// a conta_nova_s0 traz o aviso do sistema (0x02, "[通知]系统已奖励...") e a full_s1 traz
    /// as boas-vindas do canal (0x03, "-=== 欢迎来到 ..."). So' o primeiro e' que vai para a
    /// faixa do topo.
    /// </summary>
    [Fact]
    public void SoOAvisoDoSistemaVaiParaAFaixaDoTopo()
    {
        var doSistema = new byte[] { 0x02, 0x5B, 0xCD, 0xA8, 0xD6, 0xAA, 0x5D };
        var doCanal = new byte[] { 0x03, 0x2D, 0x3D, 0x3D, 0x20, 0xBB, 0xB6 };

        Assert.True(Aviso.EDoSistema(doSistema));
        Assert.False(Aviso.EDoSistema(doCanal));
        Assert.False(Aviso.EDoSistema(Array.Empty<byte>()));
    }

    /// <summary>
    /// **O NOME QUE O JOGO MOSTRA E' O NICKNAME**, e o utilizador so' serve para entrar. Sao
    /// campos diferentes no 0x0043: o utilizador em +0 e o nickname em +25.
    /// </summary>
    [Fact]
    public void ONomeVisivelEONickname()
    {
        var c = new UserStore.Account { Name = "Axia01", Nickname = "Axia" };

        Assert.Equal("Axia", c.NomeVisivel);
    }

    /// <summary>Enquanto nao houver nickname vale o utilizador — e e' esse o sinal de conta nova.</summary>
    [Fact]
    public void SemNicknameValeOUtilizador()
    {
        var c = new UserStore.Account { Name = "Axia01" };

        Assert.Equal("Axia01", c.NomeVisivel);
        Assert.True(string.IsNullOrWhiteSpace(c.Nickname));
    }

    /// <summary>Os tamanhos que faltavam — e que partiam o enquadramento, nao so' o tratamento.</summary>
    [Fact]
    public void OsQuatroTamanhosEstaoNaTabela()
    {
        Assert.Equal(28, MessageSizes.ClientToServer[BoasVindas.NicknameReq]);
        Assert.Equal(18, MessageSizes.ClientToServer[BoasVindas.PerfilReq]);
        Assert.Equal(13, MessageSizes.ServerToClient[BoasVindas.NicknameAck]);
        Assert.Equal(13, MessageSizes.ServerToClient[BoasVindas.PerfilAck]);
    }
}
