using GrooveServer.Protocol;

namespace GrooveServer.Tests;

/// <summary>
/// Os EFFECTORES, que viajam no CORPO do 0x00C3 e voltam no corpo do 0x00C4.
///
/// VETOR: `gravacoes/effectors.txt` — onze corridas da mesma musica (1st-sync, EASY, 5K), uma
/// por effector, pela ordem da lista do jogo. O corpo do 0x00C4 que o servidor original
/// devolveu e' byte a byte o do 0x00C3 nos primeiros 20; nos serviamos o gravado, todo a zeros,
/// e a escolha do jogador desaparecia ao entrar na musica.
/// </summary>
public class EffectoresTests
{
    private static byte[] Corpo(params (int Offset, byte Valor)[] campos)
    {
        var b = new byte[28];
        foreach (var (o, v) in campos) b[o] = v;
        for (int i = Effectores.Tamanho; i < b.Length; i++) b[i] = 0xAB;   // o nonce
        return b;
    }

    /// <summary>As quatro corridas do primeiro grupo: FADER BLINK, IN, OUT e FOG, no +2.</summary>
    [Theory]
    [InlineData(1, "FADER BLINK")]
    [InlineData(2, "FADER IN")]
    [InlineData(3, "FADER OUT")]
    [InlineData(4, "FOG")]
    public void OsFadersVaoNoByteDois(byte valor, string nome) =>
        Assert.Equal(nome, Effectores.Descrever(Effectores.Ler(Corpo((Effectores.FadersOffset, valor)))));

    /// <summary>E as tres do segundo: 5K MIRROR, R-SHIFT e RANDOM, no +8.</summary>
    [Theory]
    [InlineData(1, "5K MIRROR")]
    [InlineData(2, "5K R-SHIFT")]
    [InlineData(3, "5K RANDOM")]
    public void OArranjoVaiNoByteOito(byte valor, string nome) =>
        Assert.Equal(nome, Effectores.Descrever(Effectores.Ler(Corpo((Effectores.ArranjoOffset, valor)))));

    /// <summary>Sem effectores nao ha' nada a anunciar — e' o caso das nossas gravacoes todas.</summary>
    [Fact]
    public void SemEffectoresNaoHaNada() =>
        Assert.Equal("", Effectores.Descrever(Effectores.Ler(Corpo())));

    /// <summary>
    /// **SO' VINTE BYTES.** O 21º ja' e' do nonce — vale 0x0B ou 0x05 no corpo do cliente e
    /// zero no do servidor. Copiar um a mais punha lixo no que o cliente le'.
    /// </summary>
    [Fact]
    public void ONonceNaoEEcoado()
    {
        var cliente = Corpo((Effectores.FadersOffset, 4));
        var servidor = new byte[29];

        Effectores.Escrever(servidor, Effectores.Ler(cliente));

        Assert.Equal(4, servidor[Effectores.FadersOffset]);
        Assert.All(Enumerable.Range(Effectores.Tamanho, servidor.Length - Effectores.Tamanho),
                   i => Assert.Equal(0, servidor[i]));
    }

    /// <summary>O eco tem de ser byte a byte — e' o que o servidor original faz.</summary>
    [Fact]
    public void OEcoEByteAByte()
    {
        var cliente = Corpo((0, 11), (Effectores.FadersOffset, 2), (Effectores.ArranjoOffset, 3));
        var servidor = new byte[29];

        var efe = Effectores.Ler(cliente);
        Effectores.Escrever(servidor, efe);

        Assert.True(Effectores.Iguais(servidor, efe));
        Assert.Equal(cliente.Take(Effectores.Tamanho), servidor.Take(Effectores.Tamanho));
    }

    /// <summary>
    /// A corrida 11 (SPEED BAT) poe um 11 no +0, que ficou por explicar. Copia-se na mesma e
    /// anuncia-se em bruto, em vez de o interpretar — nao se inventa o que so' se viu uma vez.
    /// </summary>
    [Fact]
    public void OQueNaoSeConheceCopiaSeNaMesma()
    {
        var efe = Effectores.Ler(Corpo((0, 11)));

        Assert.Equal("+0=11", Effectores.Descrever(efe));
        Assert.Equal(11, efe[0]);
    }

    /// <summary>Corpo curto de mais nao se le' nem se escreve.</summary>
    [Fact]
    public void CorpoCurtoNaoSeToca()
    {
        Assert.False(Effectores.PodeLer(new byte[Effectores.Tamanho - 1]));
        Assert.True(Effectores.PodeLer(new byte[Effectores.Tamanho]));
    }
}
