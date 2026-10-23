# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0-beta.1] - Unreleased

First public beta (not yet published). Brings stages from *DiRT Rally 2.0* into *Assetto Corsa
Rally*, built locally on your PC from your own copies of both games.

### Added

- 30 stages across 13 locations (Argentina, Australia, Finland, Monte Carlo, Germany, Greece,
  New Zealand, Poland, Scotland, Spain, Sweden, USA and Wales), listed in `config/stages.toml`.
  Stages appear next to the vanilla ones in the stage menus.
- Per-stage generated menu images (route map, track map, height profile) and loading backdrops.
- Pace notes generated for each stage, using the game's own call tokens.
- Offline verification of every built stage and of the menu registry before installing.
- Installer script that hard-links the generated files into the game's `~mods` folder, and an
  uninstall switch that removes only files this project created (`zz_acrxs_*`).
- Repository hygiene: dual MIT OR Apache-2.0 licensing, third-party notices, contribution and
  security policies, and `scripts/check-repo-content.ps1`, which refuses game-derived files in
  the repository.