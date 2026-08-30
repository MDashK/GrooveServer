using GrooveServer.Protocol;

namespace GrooveServer.Tests;

/// <summary>
/// O nome do jogador nas mensagens gravadas, e quanto dele cabe.
///
/// Escrevia-se no maximo o comprimento do nome ANTIGO — seis, as letras de "MDashK" — e por
/// isso uma conta chamada `mariana` aparecia no jogo como `marian`. O servidor original aceita
/// nomes de 6 a 16 caracteres.
/// </summary>
public class NomeCompridoTests
{
    private static byte[] Campo(string nome, int tamanho)
    {
        var b = new byte[tamanho];
        System.Text.Encoding.ASCII.GetBytes(nome).CopyTo(b, 0);
        return b;
    }

    private static string Ler(byte[] b)
    {
        int fim = Array.IndexOf(b, (byte)0);
        return System.Text.Encoding.ASCII.GetString(b, 0, fim < 0 ? b.Length : fim);
    }

    /// <summary>O caso que se viu em jogo: `mariana` saia `marian`.</summary>
    [Fact]
    public void UmNomeDeSeteLetrasNaoSeCorta()
    {
        var campo = Campo("MDashK", 24);

        new NameRewriter("MDashK", "mariana").Apply(campo);

        Assert.Equal("mariana", Ler(campo));
    }

    /// <summary>Ate' aos 16, que e' o que o registo do servidor original aceita.</summary>
    [Fact]
    public void DezasseisLetrasCabem()
    {
        var campo = Campo("MDashK", 24);

        new NameRewriter("MDashK", "abcdefghijklmnop").Apply(campo);

        Assert.Equal("abcdefghijklmnop", Ler(campo));
        Assert.Equal(16, NameRewriter.MaximoDoNome);
    }

    /// <summary>Acima de 16 corta-se, que e' o tecto do proprio jogo.</summary>
    [Fact]
    public void AcimaDeDezasseisCortaSe()
    {
        var campo = Campo("MDashK", 32);

        new NameRewriter("MDashK", "abcdefghijklmnopQRSTUV").Apply(campo);

        Assert.Equal("abcdefghijklmnop", Ler(campo));
    }

    /// <summary>
    /// **NUNCA SE PISA O CAMPO SEGUINTE.** Se so' houver espaco ate' ao vizinho, e' ate' ai' que
    /// se escreve — o espaco util e' a corrida de NULs, nao o tecto.
    /// </summary>
    [Fact]
    public void NaoSeEscrevePorCimaDoCampoSeguinte()
    {
        var campo = new byte[] { 0x4d, 0x44, 0x61, 0x73, 0x68, 0x4b, 0, 0, 0x5a, 0x5a, 0x5a };

        new NameRewriter("MDashK", "abcdefghijkl").Apply(campo);

        // o campo ficou EXACTAMENTE cheio, portanto nao sobra NUL a terminar — le-se por bytes
        Assert.Equal("abcdefgh", System.Text.Encoding.ASCII.GetString(campo, 0, 8));
        Assert.Equal(new byte[] { 0x5a, 0x5a, 0x5a }, campo[8..]);
    }

    /// <summary>Um nome mais curto continua a encher o resto com zeros.</summary>
    [Fact]
    public void UmNomeCurtoEnchecomZeros()
    {
        var campo = Campo("MDashK", 12);

        new NameRewriter("MDashK", "ana").Apply(campo);

        Assert.Equal("ana", Ler(campo));
        Assert.All(campo[3..], b => Assert.Equal(0, b));
    }
}
