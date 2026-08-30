namespace GrooveServer.Protocol;

/// <summary>
/// Escreve listas de itens possuidos no formato do jogo.
///
/// Cada item ocupa 8 bytes: o id do catalogo (o mesmo para todos os exemplares desse
/// item) seguido do id da instancia (unico por copia possuida). As posicoes por usar
/// levam <c>0xFF</c>, que e' a marca de vazio em todo o protocolo.
///
/// O servidor NAO conhece o catalogo nem os precos — quem os tem e' o cliente, e e' ele
/// que desconta o MAX ao comprar. Ao servidor cabe apenas registar o que cada conta tem.
/// </summary>
public static class InventoryCodec
{
    public const int EntrySize = 8;
    public const byte Empty = 0xFF;

    /// <summary>
    /// Mapa do corpo do <c>InventoryInfoInf</c> (0x0044), 744 bytes.
    ///
    /// Medido na captura inv2: os itens possuidos aparecem em +316 e o item equipado em
    /// +676, e cada fronteira cai exatamente no fim de uma entrada:
    ///
    ///   0..315    coleccao (discos)   79 entradas de 4 B  [id:u16][qtd:u16]
    ///   316..675  itens possuidos     45 entradas de 8 B  [catalogo:u32][instancia:u32]
    ///   676..739  itens equipados      8 entradas de 8 B
    ///   740..743  terminador 00000000
    ///
    /// 316 + 360 + 64 + 4 = 744. A leitura anterior punha a lista em +308, oito bytes
    /// cedo, o que empurrava tudo para o separador errado no ecra' de inventario.
    /// </summary>
    public const int OwnedOffset = 316;
    public const int OwnedLimit = 676;
    public const int MountOffset = 676;
    public const int MountLimit = 740;

    /// <summary>Offset no <c>PurchaseItemAck</c> (0x00DE) onde comeca a lista.</summary>
    public const int PurchaseAckListOffset = 6;

    /// <summary>Offset no <c>SellItemAck</c> (0x00E0) onde comeca a lista. Nao e' o mesmo
    /// do PurchaseItemAck: aqui o prefixo tem so' 2 bytes, la' tem 6.</summary>
    public const int SellAckListOffset = 2;

    /// <summary>
    /// No <c>DeleteItemAck</c> (0x00DC) o **primeiro item vai partido entre o cabecalho e o
    /// corpo**, e por isso a lista parece deslocada quatro bytes:
    ///
    ///     cabecalho[5..6]  catalogo do 1º item, 16 bits BAIXOS
    ///     corpo[0..1]      catalogo do 1º item, 16 bits ALTOS
    ///     corpo[2..5]      instancia do 1º item
    ///     corpo[6..]       pares (catalogo, instancia) do 2º em diante
    ///
    /// **NAO E' INVENCAO DESTA MENSAGEM** — o <c>0x0044</c> faz o mesmo com a coleccao de
    /// discos, e por isso ha' ja' um `LerDoLogin`/`EscreverNoLogin` que trata o primeiro par a
    /// parte. Aqui viu-se por comparacao com o <c>SellItemAck</c>, que leva a mesma lista sem
    /// partir nada:
    ///
    ///     0x00E0  cab e000c1c000af12   corpo 0000 20880100 568a3401 ...
    ///     0x00DC  cab dc00d6ad00 2088  corpo 0100 568a3401 ...
    ///                              └──── 0x8820 ────┘ └─ 0x0001
    ///
    /// Nos dois o primeiro item e' o mesmo, `(100384, 20221526)`; no `0x00E0` o catalogo vai
    /// inteiro no corpo, no `0x00DC` vai `0x8820` no cabecalho e `0x0001` nos dois primeiros
    /// bytes do corpo — `0x0001_8820 = 100384`.
    ///
    /// CUSTOU TRES TENTATIVAS, e as tres viram-se em jogo. Todas tinham a mesma raiz: os bytes
    /// do primeiro catalogo ficavam os da gravacao.
    ///
    /// * lista em +6, cabecalho gravado — o cliente juntava `0x8820` do cabecalho com a nossa
    ///   instancia e ficava com um item que a conta nunca teve, impossivel de apagar. Pedia-o
    ///   com a instancia CERTA e o catalogo errado:
    ///   `(apagar: catalogo 100384, instancia 20260812; 0 removido(s))`.
    /// * lista toda em +2 — desalinhava tudo e o cliente ficava sem itens nenhuns.
    /// * lista sem o item apagado — ficava uma entrada curta e os catalogos escorregavam.
    ///
    /// O sinal em jogo era sempre o mesmo e e' util: **o primeiro item do inventario aparecia
    /// com o icone de outro** (no caso, o MAX Booster com a cara do ranger vermelho), e so'
    /// depois de apagar alguma coisa — ao entrar estava certo, porque ai' quem manda a lista
    /// e' o `0x0044`.
    /// </summary>
    public const int DeleteAckShiftOffset = 2;

    /// <summary>Marca que o catalogo de uma entrada foi apagado. Ver <see cref="WriteDeleteAck"/>.</summary>
    public const uint CatalogoApagado = 0x0000FFFF;

    /// <summary>
    /// No <c>DeleteItemAck</c>, os 16 bits BAIXOS do catalogo do primeiro item vao no
    /// CABECALHO, aqui. Ver <see cref="WriteDeleteAck"/>.
    /// </summary>
    public const int DeleteAckFirstCatalogHeaderOffset = 5;

    /// <summary>
    /// Escreve o corpo do <c>DeleteItemAck</c>.
    ///
    /// **A LISTA E' A DE ANTES DE APAGAR, e o item apagado FICA LA'** — com o catalogo trocado
    /// por <see cref="CatalogoApagado"/> e a instancia intacta. E' o que o servidor original
    /// faz, e nao e' um pormenor: mandar a lista ja' sem ele deixava-a uma entrada mais curta,
    /// e como tudo vai deslocado quatro bytes o cliente passava a ler o catalogo de cada item
    /// desencontrado do seu. Via-se assim — pedia para apagar com a instancia CERTA e um
    /// catalogo que nao era o dela:
    ///
    ///     (apagar: catalogo 100384, instancia 20260812; 0 removido(s))
    ///
    /// A instancia 20260812 era mesmo de um item da conta; o catalogo 100384 e' que nao.
    /// </summary>
    /// <param name="antes">O inventario como estava ANTES de apagar, pela mesma ordem.</param>
    public static int WriteDeleteAck(byte[] header, byte[] body,
                                     IEnumerable<(uint Catalog, uint Instance)> antes,
                                     uint catalogoApagado, uint instanciaApagada)
    {
        var todos = antes
            .Select(i => i.Catalog == catalogoApagado && i.Instance == instanciaApagada
                             ? (Catalog: CatalogoApagado, i.Instance)
                             : i)
            .ToList();

        // Monta-se a lista inteira e copia-se a partir do quarto byte. O que fica de fora sao
        // os 16 bits baixos do primeiro catalogo, que vao para o cabecalho.
        var inteira = new byte[todos.Count * EntrySize];
        WriteItems(inteira, 0, todos, inteira.Length);

        int destino = DeleteAckShiftOffset;
        int quantos = Math.Min(Math.Max(0, inteira.Length - 4), body.Length - destino);
        if (quantos > 0) Array.Copy(inteira, 4, body, destino, quantos);
        for (int i = destino + quantos; i < body.Length; i++) body[i] = Empty;

        // O CATALOGO DO PRIMEIRO ITEM VAI PARTIDO AO MEIO: os 16 bits baixos no cabecalho,
        // os altos nos dois primeiros bytes do corpo.
        ushort baixo = (ushort)(todos.Count > 0 ? todos[0].Catalog & 0xFFFF : 0xFFFF);
        ushort alto = (ushort)(todos.Count > 0 ? todos[0].Catalog >> 16 : 0xFFFF);
        if (header.Length >= DeleteAckFirstCatalogHeaderOffset + 2)
            BitConverter.TryWriteBytes(header.AsSpan(DeleteAckFirstCatalogHeaderOffset, 2), baixo);
        if (body.Length >= 2)
            BitConverter.TryWriteBytes(body.AsSpan(0, 2), alto);
        return todos.Count;
    }

    /// <summary>
    /// Offset no CABECALHO onde vai o MAX que fica depois da compra ou da venda, u16.
    ///
    /// Nao esta' no corpo. Confirmado tres vezes em captures/sell.pcapng contra o que o
    /// ecra mostrava: venda `c000 af12` = 4783, compras `af00 d30c` = 3283 e
    /// `af00 1b01` = 283.
    /// </summary>
    public const int AckBalanceHeaderOffset = 5;

    /// <summary>
    /// Escreve os itens a partir de <paramref name="offset"/>, preenchendo o resto do
    /// espaco disponivel com a marca de vazio.
    /// </summary>
    /// <param name="limit">Ate' onde pode escrever; o resto do corpo fica intacto.</param>
    public static int WriteItems(byte[] body, int offset, IEnumerable<(uint Catalog, uint Instance)> items,
                                 int limit)
    {
        int pos = offset;
        int escritos = 0;
        foreach (var (catalog, instance) in items)
        {
            if (pos + EntrySize > limit) break;
            BitConverter.TryWriteBytes(body.AsSpan(pos, 4), catalog);
            BitConverter.TryWriteBytes(body.AsSpan(pos + 4, 4), instance);
            pos += EntrySize;
            escritos++;
        }
        for (int i = pos; i < limit && i < body.Length; i++) body[i] = Empty;
        return escritos;
    }

    /// <summary>Le' os pares a partir de um offset, parando na primeira marca de vazio.</summary>
    public static List<(uint Catalog, uint Instance)> ReadItems(byte[] body, int offset, int limit)
    {
        var lista = new List<(uint, uint)>();
        for (int pos = offset; pos + EntrySize <= Math.Min(limit, body.Length); pos += EntrySize)
        {
            uint catalog = BitConverter.ToUInt32(body, pos);
            if (catalog == 0xFFFFFFFF) break;
            lista.Add((catalog, BitConverter.ToUInt32(body, pos + 4)));
        }
        return lista;
    }

    /// <summary>
    /// Gera um identificador de instancia para uma copia nova.
    ///
    /// Os observados (20220502, 20261830) parecem datas mas nao sao consistentes como
    /// tal, por isso tratam-se como opacos. Basta serem unicos dentro da conta.
    /// </summary>
    public static uint NewInstanceId(IEnumerable<uint> existentes)
    {
        uint maior = 20000000;
        foreach (var id in existentes) if (id > maior) maior = id;
        return maior + 1;
    }
}
