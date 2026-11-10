# Política de assinatura de código

**Situação atual:** os executáveis do OmniSync **ainda não são assinados**. A assinatura gratuita para projetos de código aberto está sendo solicitada à [SignPath Foundation](https://signpath.org). Quando for aprovada, os executáveis do Windows passam a sair assinados e esta página passa a trazer a atribuição: *Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).*

## O que é assinado

- Só o `OmniSync-X.Y.Z-win-x64.exe` publicado na página de [Releases](https://github.com/KuriakinToscan/OmniSync/releases) deste repositório.
- Ele é compilado pelo GitHub Actions ([`ci-release.yml`](.github/workflows/ci-release.yml)) a partir da tag `vX.Y.Z`, depois de passar pelos testes no Windows e no Linux. Nenhum arquivo compilado fora do CI é enviado para assinatura.
- Bibliotecas de terceiros embutidas no executável (veja [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)) não são assinadas em nome deste projeto.
- A chave privada do certificado fica no módulo de segurança (HSM) da SignPath. O mantenedor não tem acesso a ela.

## Papéis

| Papel | Quem |
| --- | --- |
| Autores (*committers*) | [KuriakinToscan](https://github.com/KuriakinToscan) |
| Revisores (*reviewers*) | [KuriakinToscan](https://github.com/KuriakinToscan) |
| Aprovadores (*approvers*) | [KuriakinToscan](https://github.com/KuriakinToscan) |

O código é escrito com auxílio de IA (Claude Code). Toda mudança entra por *pull request*, que o mantenedor revisa e mescla; contribuições externas também passam por *pull request* e revisão. Cada pedido de assinatura é aprovado manualmente pelo aprovador. Todos os membros usam autenticação em dois fatores no GitHub e na SignPath.

## Privacidade

Este programa não transfere nenhuma informação para outros sistemas em rede, a não ser quando o usuário pede. O OmniSync só se conecta aos servidores SFTP e às pastas de rede que o próprio usuário cadastra. Não tem telemetria, não envia relatórios de erro e não procura atualizações. Senhas ficam no cofre do sistema (Gerenciador de Credenciais do Windows ou chaveiro do Linux), nunca em arquivo de texto.

## Mudanças no sistema

A instalação na pasta do usuário, os atalhos e os agendamentos só são criados depois de confirmação na janela do programa. Para remover tudo, veja [Desinstalar](docs/MANUAL_DO_USUARIO.md#11-desinstalar) no manual.

---

## Summary (English)

- **Status:** binaries are not signed yet; free code signing from SignPath Foundation is being requested. Once approved: *Free code signing provided by SignPath.io, certificate by SignPath Foundation.*
- **Signed artifact:** only `OmniSync-X.Y.Z-win-x64.exe` on GitHub Releases, built by GitHub Actions from a `vX.Y.Z` tag of this repository. The private key stays in SignPath's HSM.
- **Team roles:** committers, reviewers and approvers: [KuriakinToscan](https://github.com/KuriakinToscan). Code is written with AI assistance (Claude Code); every change is a pull request reviewed and merged by the maintainer, and every signing request is approved manually. MFA is required for all members.
- **Privacy policy:** this program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. OmniSync only connects to SFTP servers and network shares configured by the user; it has no telemetry, crash reporting or update checks.
- **Uninstall:** see [Desinstalar](docs/MANUAL_DO_USUARIO.md#11-desinstalar) in the user manual.
