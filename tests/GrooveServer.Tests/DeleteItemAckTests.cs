using GrooveServer.Protocol;

namespace GrooveServer.Tests;

/// <summary>
/// A forma do <c>DeleteItemAck</c> (0x00DC), que custou duas tentativas a acertar.
///
/// VETOR: `gravacoes/del_s1.txt`, captura de 16/08/2026 contra o servidor original. Nessa
/// sessao o inventario (0x0044) tinha quatro itens e o jogador apagou o quarto. Comparando o
/// corpo do 0x00DC com a lista do 0x0044 da MESMA sessao, a relacao e' esta:
///
///     0x00DC[2..] == 0x0044[OwnedOffset + 4 ..]
///
/// ou seja, a lista vai deslocada quatro bytes: primeiro a INSTANCIA do item 0, e so' depois
/// os pares completos do item 1 em diante.
///
/// Escrever a lista alinhada em +6 deixava os quatro bytes da gravacao em +2, e o cliente
/// lia-os como um item — aparecia um item fantasma no lugar do apagado, impossivel de apagar
/// porque a conta nunca o teve. Escrever a lista toda em +2 desalinhava tudo e o cliente
/// ficava sem itens. Estes testes prendem o meio-termo que e' o certo.
/// </summary>
public class DeleteItemAckTests
{
    private static readonly (uint Catalog, uint Instance)[] Quatro =
    {
        (100384u, 20221526u),
        (101382u, 20261830u),
        (99332u,  20261831u),
        (107522u, 20261831u),
    };

    private static byte[] CorpoVazio() => Enumerable.Repeat((byte)0x00, 238).ToArray();
    private static byte[] CabecalhoVazio() => new byte[7];

    [Fact]
    public void OPrimeiroItemEntraSoComAInstancia()
    {
        var corpo = CorpoVazio();
        InventoryCodec.WriteDeleteAck(CabecalhoVazio(), corpo, Quatro, 107522u, 20261831u);

        Assert.Equal(20221526u, BitConverter.ToUInt32(corpo, 2));
    }

    [Fact]
    public void ODoSegundoEmDianteVaoOsParesCompletos()
    {
        var corpo = CorpoVazio();
        InventoryCodec.WriteDeleteAck(CabecalhoVazio(), corpo, Quatro, 107522u, 20261831u);

        Assert.Equal(101382u, BitConverter.ToUInt32(corpo, 6));
        Assert.Equal(20261830u, BitConverter.ToUInt32(corpo, 10));
        Assert.Equal(99332u, BitConverter.ToUInt32(corpo, 14));
        Assert.Equal(20261831u, BitConverter.ToUInt32(corpo, 18));
    }

    /// <summary>
    /// O deslocamento e' EXATAMENTE o do vetor: o corpo a partir de +2 tem de ser igual a'
    /// lista completa a partir do quarto byte.
    /// </summary>
    [Fact]
    public void ODeslocamentoEDeQuatroBytes()
    {
        var completa = new byte[Quatro.Length * InventoryCodec.EntrySize];
        var marcada = Quatro.Select(i => i.Catalog == 107522u
                                      ? (InventoryCodec.CatalogoApagado, i.Instance) : i);
        InventoryCodec.WriteItems(completa, 0, marcada, completa.Length);

        var corpo = CorpoVazio();
        InventoryCodec.WriteDeleteAck(CabecalhoVazio(), corpo, Quatro, 107522u, 20261831u);

        Assert.Equal(completa.Skip(4).ToArray(),
                     corpo.Skip(2).Take(completa.Length - 4).ToArray());
        // e os dois primeiros bytes do corpo levam a metade ALTA do catalogo do item 0
        Assert.Equal((ushort)(Quatro[0].Catalog >> 16), BitConverter.ToUInt16(corpo, 0));
    }

    /// <summary>
    /// O QUE SOBRA FICA VAZIO. Foi um resto por limpar — quatro bytes de uma gravacao — que
    /// deu o item fantasma; a partir do fim da lista nao pode ficar nada de outra sessao.
    /// </summary>
    [Fact]
    public void OQueSobraFicaMarcadoComoVazio()
    {
        var corpo = Enumerable.Repeat((byte)0x41, 238).ToArray();
        InventoryCodec.WriteDeleteAck(CabecalhoVazio(), corpo, Quatro, 107522u, 20261831u);

        int fim = 2 + Quatro.Length * InventoryCodec.EntrySize - 4;
        Assert.All(corpo.Skip(fim), b => Assert.Equal(InventoryCodec.Empty, b));
    }

    /// <summary>Sem itens nenhuns o corpo fica todo vazio, e nao com restos da gravacao.</summary>
    [Fact]
    public void ContaSemItensDaCorpoTodoVazio()
    {
        var corpo = Enumerable.Repeat((byte)0x41, 238).ToArray();
        InventoryCodec.WriteDeleteAck(CabecalhoVazio(), corpo, Array.Empty<(uint, uint)>(), 0u, 0u);

        Assert.All(corpo.Skip(InventoryCodec.DeleteAckShiftOffset),
                   b => Assert.Equal(InventoryCodec.Empty, b));
    }

    /// <summary>
    /// O CATALOGO DO PRIMEIRO ITEM VAI PARTIDO: os 16 bits baixos no cabecalho, os altos nos
    /// dois primeiros bytes do corpo. Era o que faltava, e o que fazia o primeiro item do
    /// inventario aparecer com o icone de outro depois de cada apagar.
    /// </summary>
    [Fact]
    public void OCatalogoDoPrimeiroVaiPartidoEntreCabecalhoECorpo()
    {
        var cab = CabecalhoVazio();
        var corpo = CorpoVazio();
        InventoryCodec.WriteDeleteAck(cab, corpo, Quatro, 107522u, 20261831u);

        ushort baixo = BitConverter.ToUInt16(cab, InventoryCodec.DeleteAckFirstCatalogHeaderOffset);
        ushort alto = BitConverter.ToUInt16(corpo, 0);
        Assert.Equal(Quatro[0].Catalog, (uint)(alto << 16 | baixo));
    }

    /// <summary>O vetor real: o primeiro item era o 100384 e saiu 0x8820 no cabecalho.</summary>
    [Fact]
    public void OVetorDaCapturaReproduzSe()
    {
        var cab = CabecalhoVazio();
        var corpo = CorpoVazio();
        InventoryCodec.WriteDeleteAck(cab, corpo, Quatro, 107522u, 20261831u);

        Assert.Equal(0x8820, BitConverter.ToUInt16(cab, InventoryCodec.DeleteAckFirstCatalogHeaderOffset));
        Assert.Equal(0x0001, BitConverter.ToUInt16(corpo, 0));
        Assert.Equal(20221526u, BitConverter.ToUInt32(corpo, 2));
    }
}
