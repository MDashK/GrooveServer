namespace GrooveServer.Protocol;

/// <summary>
/// Tamanhos fixos por message id.
///
/// O protocolo nao transporta comprimento: cada id tem um tamanho fixo que ambos os
/// lados conhecem. Sem esta tabela nao ha' forma de delimitar pacotes no fluxo TCP.
///
/// Origem dos valores:
///  - cliente -> servidor: literais no binario (cada XxxReq chama o serializador com
///    o tamanho constante). Validado â€” 72/72 trocos de captura partem exatamente.
///  - servidor -> cliente: deduzidos observando a cifra dessincronizar nas fronteiras
///    (ver Tools/AutoFramer). Validado â€” o stream de login parte por completo, duas
///    capturas independentes concordam, e o texto decifrado contem o nome do jogador.
///
/// Ver docs/tabela-tamanhos.md.
/// </summary>
public static class MessageSizes
{
    /// <summary>Mensagens enviadas pelo cliente.</summary>
    public static readonly IReadOnlyDictionary<ushort, int> ClientToServer = new Dictionary<ushort, int>
    {
        [0x0004] = 3,   [0x0006] = 3,   [0x000A] = 23,  [0x000F] = 70,  [0x0011] = 67,
        [0x0017] = 15,  [0x0019] = 13,  [0x001B] = 53,  [0x001D] = 15,  [0x0023] = 15,
        // O ECRA DE BOAS-VINDAS de uma conta nova: o cliente pede nickname, idade e sexo e
        // manda-os em DUAS mensagens. Medidas na `conta_nova_s0`, onde chegaram isoladas em
        // segmento, 52 segundos depois do login — o tempo de escrever na caixa.
        //
        // Faltarem estas da tabela nao era so' uma lacuna: **o enquadramento parava nelas**, e
        // por isso o `timeline` mostrava a sessao truncada e eu conclui que a caixa nao passava
        // pela rede. Passa; era a ferramenta que cegava ali.
        [0x0030] = 28,  [0x0032] = 18,
        [0x0046] = 25,  [0x004C] = 59,  [0x0053] = 12,  [0x0056] = 12,  [0x0058] = 12,
        [0x005D] = 11,  [0x005F] = 13,  [0x0064] = 11,  [0x0067] = 11,  [0x006A] = 11,
        [0x006C] = 30,  [0x006F] = 59,  [0x0072] = 259, [0x0073] = 11,  [0x0076] = 17,
        [0x009C] = 50,  [0x00A0] = 11,  [0x00A3] = 11,  [0x00A6] = 12,  [0x00B4] = 11,
        [0x00B7] = 11,  [0x00BA] = 12,  [0x00C3] = 35,  [0x00E7] = 35,  [0x00F0] = 144,
        // Apagar um amigo no DJ Messenger. Medido na captures/fail_msg.pcapng: chegou
        // isolado num segmento de 36 bytes, e o nome comeca ja' no cabecalho — os bytes
        // [3..6] sao "Evan", o principio do "Evance" que o jogador apagou.
        [0x00F2] = 36,
        // CONTINUAR um course falhado (o botao "continue" do ecra de game over).
        // Medido na mesma captura: veio a meio de um segmento de 227 bytes, colado ao
        // 0x006F e ao 0x006A, e logo a seguir aos seus 6 bytes de corpo comeca o cabecalho
        // de um 0x00F0 — 7 + 6 = 13, e a conta fecha ao byte com o fim do segmento.
        [0x0087] = 13,
        // Do course2_s1: apareceram isolados em segmento, e o palpite sobreviveu ao resto
        // do fluxo (segcheck C2S, 141 mensagens coerentes).
        [0x008A] = 11,  [0x00C5] = 19,
        // Botao do DJ Messenger no lobby. Sem este tamanho o servidor PARA a sessao com
        // "sem tamanho conhecido" e o jogo fica sem ligacao — nao e' preciso implementar a
        // funcionalidade, so' saber delimitar o pacote para o continuar a ignorar.
        // Medido em captures/speed.pcapng: isolado num segmento, 11 bytes.
        [0x0021] = 11,
        // Loja e inventario, observados em captures/inv.pcapng (compra e equipar de um
        // avatar). Chegaram todos em segmentos isolados, por isso os tamanhos sao lidos
        // diretamente e nao inferidos.
        // Course mode, medidos em captures/course.pcapng. Chegaram todos em segmentos
        // isolados de 13 bytes, por isso os tamanhos leem-se diretamente.
        [0x0083] = 13,   // escolher course; cabecalho[3] = indice
        [0x0085] = 13,   // navegar na lista de courses
        [0x00D7] = 67,   // equipar item
        [0x00DB] = 19,   // APAGAR item do inventario; mesmo tamanho e forma do vender
        [0x00DD] = 35,   // comprar item
        [0x00DF] = 19,   // vender item; cabecalho[3..6] = id do catalogo
        [0x00FD] = 13,   // pedido de info de sistema
        [0x012C] = 11,   // abrir a loja
        [0x0023] = 15,   // ja' existia; pedido de icone/avatar apos equipar
    };

    /// <summary>Mensagens enviadas pelo servidor.</summary>
    public static readonly IReadOnlyDictionary<ushort, int> ServerToClient = new Dictionary<ushort, int>
    {
        [0x0003] = 3,     [0x0007] = 3,    [0x000A] = 47,  [0x000B] = 187, [0x000C] = 27,
        [0x0010] = 92,    [0x0016] = 15,   [0x001A] = 47,  [0x0020] = 74,  [0x0025] = 73,
        // O AVISO DE SUBIDA DE NIVEL. Nao veio de captura nenhuma — nenhuma das 36 tem um
        // level up. Veio da tabela de tamanhos do PROPRIO CLIENTE, no dump de memoria; ver
        // Protocol.LevelUp e docs/tamanhos-do-cliente.txt.
        [0x0026] = 25,
        // As respostas do ecra de boas-vindas. Ver o 0x0030 e o 0x0032 do lado do cliente.
        [0x0031] = 13,    [0x0033] = 13,
        [0x0027] = 21,    [0x002A] = 199,  [0x002F] = 19,  [0x003A] = 51,
        [0x003C] = 76,
        [0x0043] = 138,   [0x0044] = 751,  [0x0045] = 953, [0x0048] = 11,  [0x004D] = 52,
        [0x0050] = 48,    [0x0051] = 135,  [0x005E] = 18,  [0x0060] = 14,  [0x0061] = 13,
        [0x0065] = 11,    [0x0068] = 11,   [0x006B] = 12,  [0x0070] = 51,  [0x0071] = 15,
        // LeaveRoomAck: a resposta ao back (0x0073) na lista de musicas. Medido em
        // captures/equip.pcapng, onde o back funcionou â€” veio sozinho num segmento TCP
        // de 12 bytes, seguido de um 0x003C. As capturas anteriores do back nunca o
        // apanharam porque nessas o jogador fez logout em vez de sair da sala, e dai'
        // a conclusao errada de que o servidor real nao respondia ao 0x0073.
        [0x0074] = 12,
        // Mensagens por seccao do inventario. Os tamanhos batem com os corpos das seccoes
        // mais os 7 do cabecalho (240+7, 120+7) — a velha teoria das cinco seccoes estava
        // errada quanto ao LAYOUT do InventoryInfoInf, mas certa quanto a estes tamanhos.
        [0x002C] = 247,   // UpdateUserInventoryShopItemInf
        [0x002E] = 127,   // UpdateUserInventoryPresentItemInf (a caixa de presentes)
        // Course mode (captures/course.pcapng). O 0x0082 e o 0x0086 vieram em segmentos
        // isolados; o 0x0084 nao, mas a resposta ao 0x0083 mediu 1424+624+107 = 2155 bytes
        // nas TRES vezes que aconteceu, o que so' bate se for uma mensagem so'.
        // 0x0082 (CourseListInf) NAO entra aqui: e' de tamanho variavel. Ver LengthInHeader.
        [0x0084] = 2155,  // detalhe do course escolhido
        [0x0086] = 13,    // confirmacao da navegacao
        // CONTINUAR um course falhado, e o DJ Messenger. Medidos na
        // captures/fail_msg.pcapng, todos em segmentos isolados: o jogador falhou o "Fine
        // Day", carregou em "continue", e antes disso abriu o DJ Messenger e apagou o amigo
        // "Evance". Ver docs/por-fazer.md.
        [0x0088] = 13,    // ContinueCourseAck — resposta ao 0x0087
        // O PREMIO DA LOTARIA DE FIM DE COURSE. O id do item ganho vai no cabecalho [3..4];
        // nas duas gravacoes do "Let's Begin" saiu 62721, que e' um dos dois do sorteio desse
        // course (Itemnum = 62465,30 e 62721,20 no CourseSection.ini). Chegou isolado num
        // segmento de 173 bytes nas duas, e o segmento seguinte comeca com um 0x002C
        // (UpdateUserInventoryShopItemInf, 247 B) — e' esse que poe o item no inventario.
        //
        // Sem este tamanho o enquadramento PARAVA aqui: tudo o que vinha depois na course_s1
        // e na course2_s1 nunca chegou a ser lido.
        //
        // SAO 19, NAO 173. O segmento tem 173 mas leva o GRUPO DE FECHO INTEIRO, e a conta
        // fecha ao byte nas duas gravacoes:
        //
        //     0x0089 (19) | 0x0025 (73) | 0x0070 (51) | 0x006B (12) | 0x005E (18) = 173
        //
        // Com 173 o enquadramento continuava a andar mas a decifra ia atras: a lista do
        // lobby saia com nomes ilegiveis e o 0x003C com indices como 40167 e niveis
        // negativos. O segcheck nao apanha isto — so' confere fronteiras de segmento, nao
        // o conteudo decifrado.
        [0x0089] = 19,
        [0x0022] = 74,    // resposta ao 0x0021 (botao do DJ Messenger)
        [0x00F3] = 13,    // resposta ao 0x00F2 (apagar amigo)
        // ATENCAO: 483 e' o tamanho medido com a lista de amigos que a conta tinha nessa
        // sessao. Se isto for a lista, o tamanho segue o numero de amigos e um valor fixo
        // nao serve — nao ha' na captura duas listas de tamanhos diferentes para o provar.
        [0x00F4] = 483,
        [0x00A7] = 12,    [0x00C4] = 36,   [0x00C9] = 70,  [0x00CA] = 20,  [0x00D3] = 33,
        [0x00FC] = 319,
        // Respostas da loja e do inventario (captures/inv.pcapng)
        [0x0024] = 25,    // UpdateUserIconInf, apos equipar
        [0x00D8] = 69,    // MountItemAck, confirma o equipar
        [0x00DC] = 245,   // confirma o apagar (captures/7k_02.pcapng)
        [0x00DE] = 253,   // PurchaseItemAck, confirma a compra
        [0x00E0] = 249,   // SellItemAck; cabecalho[5..6] = MAX que fica
        [0x00FE] = 69,    // SystemInfoAck
        [0x012D] = 16,    // resposta a abrir a loja
        [0x0018] = 13,    // LogOutAck
        // 0x007A (GameInfoInf) NAO entra aqui: e' a unica mensagem de tamanho variavel.
        // Transporta os dados da musica escolhida e o comprimento vem cifrado no proprio
        // corpo. Ver GameInfoFraming.
    };

    /// <summary>Tamanho de uma mensagem do cliente, ou null se desconhecido.</summary>
    public static int? FromClient(ushort msgId) =>
        ClientToServer.TryGetValue(msgId, out var n) ? n : null;

    /// <summary>Tamanho de uma mensagem do servidor, ou null se desconhecido.</summary>
    public static int? FromServer(ushort msgId) =>
        ServerToClient.TryGetValue(msgId, out var n) ? n : null;

    /// <summary>
    /// Mensagens do servidor que trazem o comprimento total no cabecalho, em claro.
    ///
    /// O <c>CourseListInf</c> (0x0082) e' a lista de courses disponiveis, por isso o
    /// tamanho segue o numero de courses. Media 49 bytes na captura de Marco e 55 na
    /// seguinte — o valor fixo de 49 estava ajustado a' primeira e nao passava da segunda.
    /// Quando o enquadramento parava ali, os 48 KB que o servidor mandava a seguir ficavam
    /// ilegiveis, e o timeline mostrava um servidor calado que na verdade estava a falar.
    ///
    /// Ao contrario do <c>GameInfoInf</c>, o comprimento esta' no cabecalho, que nunca e'
    /// cifrado — basta le-lo, sem mexer no estado da cifra.
    /// </summary>
    /// <remarks>
    /// O <c>0x0020 UserIDInfoInf</c> entrou aqui depois de uma sessao com o DJ Messenger
    /// vazio: mede 74 bytes com um amigo na lista e 7 (so' cabecalho) sem nenhum, e nos dois
    /// casos o <c>[3..6]</c> traz exactamente o seu proprio tamanho. Com o valor fixo de 74,
    /// a sessao limpa desenquadrava-se logo na segunda mensagem do login.
    /// </remarks>
    /// <remarks>
    /// O <c>0x0039 OnChatInf</c> — a barra de notificacao do jogo — entrou aqui depois de o
    /// enquadramento da course_s1 morrer 1600 bytes antes do fim, e de esses 1600 bytes serem
    /// exactamente onde estava a subida de nivel que se andava a procurar. A tabela de
    /// tamanhos do PROPRIO CLIENTE (docs/tamanhos-do-cliente.txt) da'-lhe 0xFFFFFFFF, que e' a
    /// marca de variavel; tinhamos 53 fixo, medido no unico que se via, o do login.
    ///
    /// Confirmado em 19 gravacoes: TODOS os 0x0039 trazem o proprio tamanho em [3..6]. O do
    /// login vale sempre 53 — dai' o valor fixo ter funcionado tanto tempo. O da course_s1
    /// vale 77, e e' o aviso de que o sistema ofereceu um item.
    /// </remarks>
    public static readonly ushort[] LengthInHeader = { 0x0082, 0x0020, 0x0039 };

    /// <summary>Offset do campo de comprimento dentro do cabecalho, para <see cref="LengthInHeader"/>.</summary>
    public const int HeaderLengthOffset = 3;

    /// <summary>
    /// Tamanho de uma mensagem do servidor em <paramref name="offset"/>, consultando o
    /// cabecalho quando o id e' de tamanho variavel. Use-se esta sobrecarga sempre que o
    /// stream esteja a' mao; a que so' recebe o id nao sabe enquadrar o 0x0082.
    /// </summary>
    public static int? FromServer(ushort msgId, byte[] stream, int offset)
    {
        if (Array.IndexOf(LengthInHeader, msgId) < 0) return FromServer(msgId);
        if (offset + PacketCodec.HeaderSize > stream.Length) return null;

        int total = BitConverter.ToInt32(stream, offset + HeaderLengthOffset);
        // Um comprimento absurdo quer dizer que o enquadramento ja' vinha errado; mais
        // vale parar e dizer que nao se sabe do que avancar por um valor inventado.
        return total >= PacketCodec.HeaderSize && total <= 64 * 1024 ? total : null;
    }
}


