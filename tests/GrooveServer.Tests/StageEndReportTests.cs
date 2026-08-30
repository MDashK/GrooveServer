using GrooveServer.Protocol;

namespace GrooveServer.Tests;

/// <summary>
/// A BARRA DE HP NO FIM DA MUSICA, que e' como se sabe se ela foi abaixo.
///
/// Vive no <c>0x006F</c> que o cliente manda quando a musica acaba, num float em +30. O
/// servidor le'-a para nao dar XP nem MAX a quem perdeu.
///
/// **VALE NOS DOIS MODOS, e essa e' a razao destes testes.** O servidor so' a lia quando a
/// sala era de course (`if (id == ... && _courseRoom && ...)`), e por isso um game over no
/// FREE MODE passava por jogada completa: o `XpDaJogada` tem chao de 20 pontos, e o MAX,
/// quando o cliente reporta zero ganho, caia na media de 15. Dava para subir de nivel a
/// perder.
///
/// VETOR: `gravacoes/end_s1.txt`, contra o servidor original. Dez musicas de FREE MODE
/// seguidas — cinco concluidas e cinco com game over. O que o servidor real concedeu, lido no
/// <c>0x0070</c> (+37 XP, +39 MAX):
///
/// | musica | barra em +30 | XP | MAX |
/// |---|---|---|---|
/// | 1..5 | 100,00 | 37, 39, 38, 38, 28 | 13, 15, 18, 10, 17 |
/// | 6..10 | 0,00 | 0 | 0 |
///
/// E o <c>0x0025</c> confirma-o do outro lado: o XP parou nos 180 e o MAX nos 14895, e nao
/// voltaram a mexer nas cinco ultimas.
/// </summary>
public class StageEndReportTests
{
    /// <summary>Musica 1 de `end_s1`: concluida, barra cheia.</summary>
    private const string Concluida =
        "0000ED00CF5CCF5CCF5CCF5CCF5CCF5CCF5CCF5CCF5CCB5CCB5CDB5C1E5C0000C842";

    /// <summary>Musica 6 de `end_s1`: game over em free mode, barra a zero.</summary>
    private const string GameOver =
        "00002201245CE35CC45CCF5CCF5CCF5CCF5CCF5CCF5CCF5CCF5CCF5CCF5C00000000";

    [Theory]
    [InlineData(Concluida, 100f)]
    [InlineData(GameOver, 0f)]
    public void ABarraEstaEmTrintaEEUmFloat(string corpoHex, float esperada)
    {
        var corpo = Convert.FromHexString(corpoHex);

        Assert.True(StageEndReport.PodeLerBarra(corpo));
        Assert.Equal(esperada, StageEndReport.LerBarra(corpo));
    }

    /// <summary>
    /// O criterio de falhada e' a igualdade exacta a zero, e nao um limiar. Uma musica
    /// concluida a raspar ainda traz a barra acima de zero.
    /// </summary>
    [Theory]
    [InlineData(Concluida, false)]
    [InlineData(GameOver, true)]
    public void SoAZeroEQueEFalhada(string corpoHex, bool falhada)
    {
        var corpo = Convert.FromHexString(corpoHex);

        Assert.Equal(falhada, StageEndReport.LerBarra(corpo) == 0f);
    }

    /// <summary>
    /// Um corpo curto de mais nao se le' — mais vale nao decidir do que decidir sobre lixo. Se
    /// isto falhasse, um relatorio truncado passava por game over e ninguem ganhava nada.
    /// </summary>
    [Fact]
    public void CorpoCurtoNaoSeLe()
    {
        Assert.False(StageEndReport.PodeLerBarra(new byte[StageEndReport.BarraOffset + 3]));
        Assert.True(StageEndReport.PodeLerBarra(new byte[StageEndReport.BarraOffset + 4]));
    }
}
