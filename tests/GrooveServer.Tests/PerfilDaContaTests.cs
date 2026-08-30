using GrooveServer.Net;
using GrooveServer.Protocol;

namespace GrooveServer.Tests;

/// <summary>
/// O painel de perfil de uma conta nova, que estava a mostrar dados de outra pessoa.
///
/// Uma conta acabada de criar aparecia com 453 de combo maximo, 100,00% de melhor precisao e
/// 87,27% de media — os numeros de quem gravou a captura — e com "M" no sexo mesmo tendo
/// escolhido feminino.
/// </summary>
public class PerfilDaContaTests
{
    /// <summary>
    /// O NICKNAME COMPLETO. O cabecalho sao sete bytes e so' sete: apanhar quatro a mais
    /// trazia bytes ainda cifrados, e `candido5566` chegava como `cand?3?4ido5566`.
    /// </summary>
    [Fact]
    public void ONicknameNaoTrazLixoDoMeio()
    {
        var pacote = new byte[] { 0x30, 0x00, 0x0a, (byte)'c', (byte)'a', (byte)'n', (byte)'d',
                                  0xDE, 0xAD, 0xBE, 0xEF };   // do 7 em diante ja' e' cifrado
        var corpo = System.Text.Encoding.ASCII.GetBytes("ido5566\0\0\0");

        Assert.Equal("candido5566", BoasVindas.LerNickname(pacote, corpo));
    }

    /// <summary>Com quatro letras o nome acaba no cabecalho — foi por isso que o erro passou.</summary>
    [Fact]
    public void UmNomeDeQuatroLetrasContinuaACertar()
    {
        var pacote = new byte[] { 0x30, 0x00, 0x0a, 0x41, 0x78, 0x69, 0x61, 0xDE, 0xAD };

        Assert.Equal("Axia", BoasVindas.LerNickname(pacote, new byte[] { 0, 0, 0 }));
    }

    /// <summary>
    /// O SEXO MUDA DE CONTAGEM entre mensagens: o cliente manda 1 para feminino, o painel
    /// espera 0. Servir o numero do cliente tal e qual punha "M" numa conta feminina.
    /// </summary>
    [Theory]
    [InlineData(1, 0)]    // feminino
    [InlineData(2, 1)]    // masculino
    public void OSexoDoPainelE0Baseado(int doCliente, byte noPainel)
    {
        var c = new UserStore.Account { Sexo = doCliente };

        Assert.Equal(noPainel, (byte)(c.Sexo - 1));
    }

    /// <summary>O avatar de partida segue o sexo. A conta feminina da captura nasceu com 0xF001.</summary>
    [Theory]
    [InlineData(1, UserInfo.AvatarFeminino)]
    [InlineData(2, UserInfo.AvatarMasculino)]
    public void OAvatarDePartidaSegueOSexo(int sexo, ushort esperado) =>
        Assert.Equal(esperado, sexo == 1 ? UserInfo.AvatarFeminino : UserInfo.AvatarMasculino);

    /// <summary>Uma conta sem musicas jogadas mostra zeros, nao os numeros de outra pessoa.</summary>
    [Fact]
    public void ContaNovaNaoTemNumerosDeNinguem()
    {
        var c = new UserStore.Account();

        Assert.Equal(0, c.MaxCombo);
        Assert.Equal(0, c.MelhorPrecisao);
        Assert.Equal(0, c.PrecisaoMedia);
    }

    /// <summary>O combo e a melhor precisao guardam o MAIOR; a media e' mesmo media.</summary>
    [Fact]
    public void AsActuacoesAcumulamComoDevem()
    {
        var c = new UserStore.Account();

        c.RegistarActuacao(90.0, 200);
        c.RegistarActuacao(80.0, 300);
        c.RegistarActuacao(95.0, 100);

        Assert.Equal(300, c.MaxCombo);
        Assert.Equal(95.0, c.MelhorPrecisao);
        Assert.Equal(88.33, Math.Round(c.PrecisaoMedia, 2));
    }

    /// <summary>Os tres offsets do painel, e o do sexo que faltava.</summary>
    [Fact]
    public void OsOffsetsDoPainel()
    {
        Assert.Equal(109, UserInfo.CreditosOffset);
        Assert.Equal(50, UserProperty.CreditosOffset);
        Assert.Equal(50, UserInfo.SexoOffset);
        Assert.Equal(51, UserInfo.AnoNascimentoOffset);
        Assert.Equal(93, UserInfo.MaxComboOffset);
        Assert.Equal(97, UserInfo.BestAccuracyOffset);
        Assert.Equal(101, UserInfo.AvgAccuracyOffset);
    }

    /// <summary>
    /// O NICKNAME TEM TECTO DE 12 no cliente, contra os 16 do utilizador — o cliente nao deixa
    /// escrever mais. O campo do 0x0010 leva 17 (o til mais 16), portanto ha' folga de sobra e
    /// nao ha' nada a limitar do nosso lado; fica registado porque a assimetria surpreende.
    /// </summary>
    [Fact]
    public void OCampoDoNicknameTemFolgaParaOTectoDoCliente()
    {
        Assert.Equal(17, Credentials.AckNicknameTamanho);
        Assert.True(Credentials.AckNicknameTamanho > 12);
        Assert.Equal(16, NameRewriter.MaximoDoNome);
    }

    /// <summary>
    /// **OS DOIS AVATARES DE OMISSAO NAO SAO ITENS.** Uma conta nova nao tem avatar nenhum no
    /// inventario, e a logica antiga exigia um item equipado — caia no masculino e, pior,
    /// escrevia 0 por cima. Uma conta feminina perdia o avatar no primeiro login.
    /// </summary>
    [Theory]
    [InlineData(UserInfo.AvatarFeminino)]
    [InlineData(UserInfo.AvatarMasculino)]
    public void OsAvataresDeOmissaoNaoPrecisamDeItem(ushort avatar)
    {
        var c = new UserStore.Account { Avatar = avatar };

        Assert.Empty(c.Items);
        Assert.NotEqual(0, c.Avatar);
        Assert.True(avatar == UserInfo.AvatarFeminino || avatar == UserInfo.AvatarMasculino);
    }

    /// <summary>
    /// **O PAINEL E' REDESENHADO PELO 0x0025**, nao pelo 0x0043 do login. Era por isso que uma
    /// conta nova, depois de jogar uma musica, voltava aos numeros de quem gravou a captura —
    /// 453 de combo e 99,94% — e fechar o jogo "corrigia": voltava a valer o 0x0043.
    ///
    /// Os tres offsets batem com o painel do MDashK na `end_s1`.
    /// </summary>
    [Fact]
    public void OsTresCamposDoPainelNoFimDeMusica()
    {
        var corpo = new byte[80];
        BitConverter.TryWriteBytes(corpo.AsSpan(UserProperty.MaxComboOffset, 4), 453u);
        BitConverter.TryWriteBytes(corpo.AsSpan(UserProperty.MelhorPrecisaoOffset, 4), 9994u);
        BitConverter.TryWriteBytes(corpo.AsSpan(UserProperty.PrecisaoMediaOffset, 2), (ushort)9124);

        Assert.Equal(new[] { 34, 38, 42 },
                     new[] { UserProperty.MaxComboOffset, UserProperty.MelhorPrecisaoOffset,
                             UserProperty.PrecisaoMediaOffset });
        Assert.Equal(453u, BitConverter.ToUInt32(corpo, 34));
        Assert.Equal(9994u, BitConverter.ToUInt32(corpo, 38));
        Assert.Equal(9124, BitConverter.ToUInt16(corpo, 42));
    }

    /// <summary>Nenhum dos campos do 0x0025 pisa outro.</summary>
    [Fact]
    public void OsCamposDoFimDeMusicaCabemTodos()
    {
        (int Off, int Tam)[] campos =
        {
            (UserProperty.RecordeOffset, 4), (UserProperty.RecordeRankingOffset, 4),
            (UserProperty.MaxComboOffset, 4), (UserProperty.MelhorPrecisaoOffset, 4),
            (UserProperty.PrecisaoMediaOffset, 2), (UserProperty.RecordeRanking7KOffset, 4),
        };
        foreach (var (a, ta) in campos)
            foreach (var (b, tb) in campos)
                if (a != b) Assert.True(a + ta <= b || b + tb <= a, $"+{a} e +{b} sobrepoem-se");
    }
}
