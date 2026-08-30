# Tabela de tamanhos — DJMAX Online

O protocolo **não tem campo de comprimento**. Cada message id tem um tamanho fixo que
ambos os lados conhecem por tabela. Foi isso que tornou o enquadramento difícil de
descobrir: procurei um campo que não existe.

Prova empírica: na captura, todos os msgid observados isoladamente mais do que uma vez
apareceram sempre com o mesmo tamanho — `0x6c` em 31 ocorrências, sempre 30 bytes.

Estrutura do pacote:

```
[0..1]  message id (uint16 LE)
[2]     byte de chave por-pacote (PRNG lagged-Fibonacci)
[3..6]  campos específicos da mensagem
[7..]   corpo — cifrado se o tamanho total >= 8; em claro se < 8
```

## Cliente → servidor

Extraída dos literais no binário: cada função `XxxReq` chama o serializador
`FUN_0043a420(tamanho, buffer)` com o tamanho como constante, e escreve o msgid em
`buffer[0]`.

**Validação: 72 de 72 troços de captura partem exatamente com esta tabela.**

| msgid | Mensagem | Tamanho |
|---|---|---|
| `0x0004` | PingTestInf | 3 |
| `0x0006` | AliveAck | 3 |
| `0x000A` | ConnectReq | 23 |
| `0x000F` | AuthenticateInReq | 70 |
| `0x0011` | AuthenticateInACCReq | 67 |
| `0x0017` | KeepAuthenticateInReq | 15 |
| `0x0019` | LogOutReq | 13 |
| `0x001B` | LogInReq | 53 |
| `0x001D` | UserInfoReq | 15 |
| `0x0023` | — | 15 |
| `0x0046` | JoinRoomReq | 25 |
| `0x004C` | CreateRoomReq | 59 |
| `0x0053` | — | 12 |
| `0x0056` | — | 12 |
| `0x0058` | TeamControlReq | 12 |
| `0x005D` | ReadyReq | 11 |
| `0x005F` | StartReq | 13 |
| `0x0064` | PlayStartReq | 11 |
| `0x0067` | PlaySkipReq | 11 |
| `0x006A` | PlayOverReq | 11 |
| `0x006C` | PlayStateInf | 30 |
| `0x006F` | StageResultInf | 59 |
| `0x0072` | — | 259 |
| `0x0073` | LeaveRoomReq | 11 |
| `0x0076` | ChangeDiscReq | 17 |
| `0x009C` | RoomChangeInfoReq | 50 |
| `0x00A0` | QuickInviteReq | 11 |
| `0x00A3` | — | 11 |
| `0x00A6` | InviteRejectReq | 12 |
| `0x00B4` | GetItemReq | 11 |
| `0x00B7` | ItemLevelUpReq | 11 |
| `0x00BA` | UseItemReq | 12 |
| `0x00C3` | UseEffectorInf | 35 |
| `0x00E7` | — | 35 |
| `0x00F0` | — | 144 |

## Servidor → cliente

Descoberta observando a própria cifra denunciar as fronteiras: decifrando em contínuo
para lá do fim de um pacote, os 7 bytes de cabeçalho do pacote seguinte — que nunca
foram cifrados — entram na cifra e dessincronizam o estado. O enchimento (`0x00`/`0xCC`)
desaba de ~98% para ~0%. O ponto de colapso é a fronteira.

**Validação:** o stream de login (3404 bytes) decompõe-se por completo sem sobras, duas
capturas independentes dão fronteiras idênticas, e o texto decifrado contém o nome do
jogador e o seu total de MAX como inteiro de 32 bits.

| msgid | Mensagem | Tamanho | Nota |
|---|---|---|---|
| `0x0003` | PingTestInf | 3 | |
| `0x0007` | AliveReq | 3 | |
| `0x000A` | ConnectAck | 47 | entrega a chave de sessão no offset 7 |
| `0x001A` | LogInAck | 47 | status `0x2B` = sucesso |
| `0x0020` | UserIDInfoInf | 74 | |
| `0x0039` | ChatInf | 53 | |
| `0x003A` | RoomInfoUpdateInf | 51 | |
| `0x003C` | WaiterInfoUpdateInf | 76 | |
| `0x0043` | UserInfoInf | 138 | contém nome, nível e MAX |
| `0x0044` | InventoryInfoInf | 751 | |
| `0x0045` | MessengerInfoInf | 953 | |
| `0x0048` | PostJoinRoomInf | 11 | |
| `0x004D` | CreateRoomAck | 52 | |
| `0x0050` | RoomDescInf | 48 | |
| `0x0051` | — | 135 | |
| `0x0065` | PlayStartInf | 11 | |
| `0x0071` | CheckDataReq | 15 | |
| `0x007A` | GameInfoInf | 2741 | **incerto** — ver abaixo |
| `0x00A7` | InviteRejectAck | 12 | |
| `0x00FC` | EnvironmentInf | 319 | pares chave/valor de configuração |

### Incerteza no `GameInfoInf`

O valor 2741 passou o critério de pontuação, mas logo a seguir o stream apresenta um
msgid `0x016A`, que não existe no dispatcher extraído do cliente. Ou o tamanho está
errado, ou há message ids tratados fora do `switch` principal. Não usar sem confirmar.

## Conteúdo observado

O `EnvironmentInf` transporta pares chave/valor:

- `MIX_FILTER` = `EZ;NM;HD;MX;SC` — as dificuldades
- `LOADURL` = um endereço FTP de onde o cliente descarrega as músicas

O segundo é relevante para o objetivo: **a lista de músicas não vem no protocolo**, vem
desse repositório. Um servidor próprio teria de apontar o cliente para um repositório
equivalente, ou servir os ficheiros que já existem localmente nos `.pak`.
