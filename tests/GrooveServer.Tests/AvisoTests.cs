using GrooveServer.Protocol;
using Xunit;

namespace GrooveServer.Tests;

/// <summary>
/// As notificacoes do sistema, traduzidas. As amostras sao os bytes exactos das gravacoes.
/// </summary>
public class AvisoTests
{
    /// <summary>O aviso do item de boas-vindas, tal como veio da conta_nova_s0 (炫紫MK2).</summary>
    private static byte[] Premio(params byte[] nome)
    {
        var pre = new byte[] { 0x02, 0x5B, 0xCD, 0xA8, 0xD6, 0xAA, 0x5D, 0xCF, 0xB5, 0xCD, 0xB3,
                               0xD2, 0xD1, 0xBD, 0xB1, 0xC0, 0xF8, 0xC4, 0xFA, 0x20 };
        var suf = new byte[] { 0x20, 0xB5, 0xC0, 0xBE, 0xDF, 0xA3, 0xAC, 0xC7, 0xEB, 0xB5, 0xBD,
                               0xC4, 0xFA, 0xB5, 0xC4, 0xB5, 0xC0, 0xBE, 0xDF, 0xCF, 0xE4, 0xC1,
                               0xEC, 0xC8, 0xA1, 0xA3, 0xAC, 0xD7, 0xA3, 0xC4, 0xFA, 0xD3, 0xCE,
                               0xCF, 0xB7, 0xD3, 0xE4, 0xBF, 0xEC, 0xA1, 0xA3, 0x00 };
        return pre.Concat(nome).Concat(suf).ToArray();
    }

    private static string Ler(byte[] corpo) =>
        System.Text.Encoding.ASCII.GetString(corpo, 1, corpo.Length - 2);

    /// <summary>O nome do item passa pela tabela.</summary>
    [Fact]
    public void OPremioSaiEmInglesComONomeTraduzido()
    {
        var corpo = Premio(0xEC, 0xC5, 0xD7, 0xCF, 0x4D, 0x4B, 0x32);   // 炫紫MK2

        var fora = Aviso.EmIngles(corpo, _ => "Violet_MK2");

        Assert.NotNull(fora);
        Assert.Equal(Aviso.DoSistema, fora![0]);
        Assert.Equal("[Notice] You received the item Violet_MK2. " +
                     "Please collect it from your item box. Have fun!", Ler(fora));
    }

    /// <summary>
    /// Um item que a tabela nao conheca nao impede a traducao: a frase fica em ingles e o nome
    /// fica como veio. Melhor meia traducao do que nenhuma.
    /// </summary>
    [Fact]
    public void UmNomeDesconhecidoNaoTravaAFrase()
    {
        var corpo = Premio(0x4C, 0x65, 0x65);   // "Lee", que ja' vem em latim

        var fora = Aviso.EmIngles(corpo, _ => null);

        Assert.NotNull(fora);
        Assert.Contains("the item Lee.", Ler(fora!));
    }

    /// <summary>As boas-vindas do canal, da full_s1. O nome do canal ja' vem em ASCII.</summary>
    [Fact]
    public void OCanalTambemSeTraduz()
    {
        var pre = new byte[] { 0x03, 0x2D, 0x3D, 0x3D, 0x20, 0xBB, 0xB6, 0xD3, 0xAD, 0xC0, 0xB4,
                               0xB5, 0xBD, 0x27 };
        var suf = new byte[] { 0x27, 0xC6, 0xB5, 0xB5, 0xC0, 0x2E, 0x20, 0x3D, 0x3D, 0x2D, 0x00 };
        var corpo = pre.Concat(System.Text.Encoding.ASCII.GetBytes("LIGHT/.[5KEY] Classic"))
                       .Concat(suf).ToArray();

        var fora = Aviso.EmIngles(corpo, _ => null);

        Assert.NotNull(fora);
        Assert.Equal(Aviso.DoCanal, fora![0]);
        Assert.Equal("-== Welcome to the 'LIGHT/.[5KEY] Classic' channel. ==-", Ler(fora));
    }

    /// <summary>
    /// Um texto que nao seja de um molde conhecido devolve null, para passar tal e qual. E' a
    /// diferenca entre nao traduzir e estragar.
    /// </summary>
    [Fact]
    public void OQueNaoSeReconheceFicaComoEsta()
    {
        Assert.Null(Aviso.EmIngles(new byte[] { 0x02, 0x41, 0x42, 0x43, 0x00 }, _ => null));
        Assert.Null(Aviso.EmIngles(new byte[] { 0x02 }, _ => null));
    }
}
