namespace GrooveServer.Protocol;

/// <summary>
/// A VERSAO QUE O CLIENTE ANUNCIA, e a unica coisa do protocolo que muda entre a build
/// original da SNDA (2007) e a nossa (2019).
///
/// Viaja no <c>ConnectReq</c> (<c>0x000A</c>, 23 bytes), em <c>[3..6]</c> e em claro — os
/// mesmos quatro bytes que a documentacao descreve como "campos especificos da mensagem".
/// Confirmado contra captura real (gravacoes/course_s1.txt, primeiro C2S):
///
///     0a00 77 01020400 0000000000000000 ffffffff 0e0000
///       |   |  \_ versao = 0x00040201
///       |   \_ byte de chave
///       \_ msgid 0x000A
///
/// DE ONDE VEM O VALOR. No exe original de 2007 esta' gravado no codigo:
///
///     0x004318EF   c7 45 e7 06 00 02 00    mov dword ptr [ebp-0x19], 0x00020006
///
/// A nossa build troca esses sete bytes por <c>push ebp; call 0x10003E70; nop</c>, e o
/// <c>0x10003E70</c> e' o <c>A_Very_NB_s_function</c> do <c>DJMax.dll</c>, que escreve nesse
/// mesmo lugar o dword de <c>DJMax.dll+0x430DC</c> — <c>0x00040201</c>. E' o valor que
/// tambem alimenta o <c>"DJMax Online v%x.%x T%d"</c> do overlay, o que da' "v4.2 T1".
/// Quem montou o servidor privado po-lo numa DLL para poder subir a versao sem tocar no exe,
/// que esta' empacotado com ASProtect.
///
/// O QUE ISTO OBRIGA A FAZER. O cliente regista os tamanhos das mensagens que ESPERA RECEBER
/// chamando <c>FUN_0043A680(id, tamanho)</c> uma vez por id, a partir de <c>0x004300A3</c>.
/// Extraidas as duas tabelas dos dois despejos de memoria, tem 111 entradas cada e
/// **110 sao iguais**. A unica que muda e' precisamente o ConnectAck:
///
///     2007 (0x00020006):   push 0x2f ; push 9    -> id 0x0009, 47 bytes
///     2019 (0x00040201):   push 0x2f ; push 0xa  -> id 0x000A, 47 bytes
///
/// Por isso o cliente de 2007 ficava parado no popup "a ligar ao servidor": recebia o
/// ConnectAck com o id errado e nunca chegava a tratar dele. Servir o MESMO pacote com o id
/// <c>0x0009</c> resolve — o corpo, a chave de sessao e tudo o resto sao iguais.
///
/// Ver docs/por-fazer.md, seccao V8.
/// </summary>
public static class ClientVersion
{
    /// <summary>Onde a versao esta' dentro do ConnectReq.</summary>
    public const int Offset = 3;

    /// <summary>A build do servidor privado chines que temos usado — "v4.2 T1".</summary>
    public const uint Nossa = 0x00040201;

    /// <summary>O cliente original da SNDA (DJMax 2.50 / 2.60), gravado no proprio exe.</summary>
    public const uint Snda260 = 0x00020006;

    /// <summary>O id com que a nossa build espera o ConnectAck.</summary>
    public const ushort IdConnectAckPadrao = 0x000A;

    /// <summary>O id com que as builds anteriores a' 4.x esperam o ConnectAck.</summary>
    public const ushort IdConnectAckAntigo = 0x0009;

    /// <summary>Le' a versao do ConnectReq. Devolve 0 se o pacote for curto de mais.</summary>
    public static uint Ler(ReadOnlySpan<byte> connectReq) =>
        connectReq.Length >= Offset + 4 ? BitConverter.ToUInt32(connectReq[Offset..]) : 0u;

    /// <summary>
    /// Com que id se serve o ConnectAck a este cliente. A fronteira e' a versao maior: a
    /// tabela de tamanhos so' foi medida em duas builds (0x00020006 e 0x00040201), por isso
    /// so' se afirma o que se mediu — 4.x e acima usam o <c>0x000A</c>, o resto o <c>0x0009</c>.
    /// Versao 0 (pacote curto ou desconhecida) fica no padrao, que e' o caso comum.
    /// </summary>
    public static ushort IdDoConnectAck(uint versao) =>
        versao != 0 && (versao >> 16) < 4 ? IdConnectAckAntigo : IdConnectAckPadrao;

    /// <summary>
    /// E' uma build da era de 2007, com o catalogo de musicas pequeno?
    ///
    /// A mesma fronteira do <see cref="IdDoConnectAck"/> — versao maior abaixo de 4 — e por
    /// isso a mesma reserva: so' se mediram duas builds. O que isto decide e' se os ids de
    /// musica precisam de traducao, porque o id de rede e' a POSICAO no `DiscStock.csv` do
    /// cliente e o de 2007 tem 94 musicas onde o nosso tem 277. Ver Net.SongIdMap.
    /// </summary>
    public static bool EDe2007(uint versao) => versao != 0 && (versao >> 16) < 4;

    /// <summary>Como o cliente escreve a versao no ecra: <c>v%x.%x T%d</c>.</summary>
    public static string Nome(uint versao) =>
        versao == 0 ? "versao desconhecida"
                    : $"v{versao >> 16:x}.{(versao >> 8) & 0xFF:x} T{versao & 0xFF}";
}
