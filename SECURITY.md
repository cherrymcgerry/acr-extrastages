# Security policy

## Reporting a vulnerability

Please **don't open a public issue** for security problems (for example, a way the tools could be
abused to run untrusted code, a path-traversal bug when reading a spec or archive, or a release
file that doesn't match its published checksum).

Report it privately through GitHub instead: on the repository page, open **Security → Report a
vulnerability** ([private advisory form](https://github.com/cherrymcgerry/acr-extrastages/security/advisories/new)).
Include what you found, the ACR Extra Stages version, and how to reproduce it.

## Scope

ACR Extra Stages is an offline tool: it reads your own game installs and writes files into the
game's `~mods` folder. It does not open network connections except to download its own
prerequisites when you ask it to (see [README.md](README.md)). It never injects code into the
running game.

## Supported versions

Only the latest release gets fixes. ACR Extra Stages is a hobby project, so there is no guaranteed
response time, but reports are taken seriously.