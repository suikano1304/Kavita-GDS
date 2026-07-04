# [<img src="/Logo/kavita.svg" width="32" alt="">]() Kavita
<div align="center">

![new_github_preview_stills](https://github.com/user-attachments/assets/f016b34f-3c4c-4f07-8e72-12cd6f4e71ea)

Kavita is a fast, feature rich, cross-platform reading server. Built with a focus for being a full solution for all your reading needs. Set up your own server and share
your reading collection with your friends and family!

[![Release](https://img.shields.io/github/release/Kareadita/Kavita.svg?style=flat&maxAge=3600)](https://github.com/Kareadita/Kavita/releases)
[![License](https://img.shields.io/badge/license-GPLv3-blue.svg?style=flat)](https://github.com/Kareadita/Kavita/blob/master/LICENSE)
[![Downloads](https://img.shields.io/github/downloads/Kareadita/Kavita/total.svg?style=flat)](https://github.com/Kareadita/Kavita/releases)
[![Docker Pulls](https://img.shields.io/docker/pulls/jvmilazz0/kavita.svg)](https://hub.docker.com/r/jvmilazz0/kavita)
[![Maintainability Rating](https://sonarcloud.io/api/project_badges/measure?project=Kareadita_Kavita&metric=sqale_rating)](https://sonarcloud.io/dashboard?id=Kareadita_Kavita)
[![Security Rating](https://sonarcloud.io/api/project_badges/measure?project=Kareadita_Kavita&metric=security_rating)](https://sonarcloud.io/dashboard?id=Kareadita_Kavita)
[![Backers on Open Collective](https://opencollective.com/kavita/backers/badge.svg)](#backers)
[![Sponsors on Open Collective](https://opencollective.com/kavita/sponsors/badge.svg)](#sponsors)
<a href="https://hosted.weblate.org/engage/kavita/">
<img src="https://hosted.weblate.org/widgets/kavita/-/ui/svg-badge.svg" alt="Translation status" />
</a>
<img src="https://img.shields.io/endpoint?url=https://stats.kavitareader.com/api/ui/shield-badge"/>
</div>

## Kavita-GDS Fork Release

This fork publishes GDS-focused Kavita builds through GHCR. The current public release is `0.9.0.12-6`, based on the official Kavita `0.9.0.12` nightly source plus the GDS patch stack.

```bash
docker pull ghcr.io/suikano1304/kavita-gds:0.9.0.12-6
```

The `0.9.0.12-6` release includes:

- Streaming GDS scan-phase grouping that avoids retaining every per-directory scan result and parser list until the whole root is parsed.
- Lower-memory GDS processing that releases parsed series metadata after each skipped or processed series.
- GDS processing queue compaction that stores only series indexes while processing and clears metadata caches after each scan.
- Representative-cover generation during scans, with full per-volume/per-chapter cover generation available through metadata refresh paths.
- "Last Modified" sorting that treats newly added content as content activity, so newly imported files with old filesystem timestamps can still surface correctly.
- GDS scan fingerprints so unchanged series can skip expensive processing without relying on folder mtime skip behavior.
- Content-file based "Last Modified" sorting and JumpBar dates, separate from DB entity update time.
- Streaming `kavita.yaml`/`kavita.yml` metadata reads during scan passes, including large sidecar files, without loading unused cover payloads.
- GDS page-count hints from sidecar metadata and filename page markers to avoid unnecessary archive opens during broad scans.
- Stable GDS fingerprint matching for mixed-format series so unchanged mixed PDF/archive/text groups do not reprocess on every scan.
- Korean part/volume parsing for series that intentionally contain separate part folders under the same normalized title.
- Multi-arch GHCR images for `linux/amd64`, `linux/arm64`, and `linux/arm/v7`.

See [RELEASE_NOTES.md](RELEASE_NOTES.md), [docs/BUILD_NOTES_KO.md](docs/BUILD_NOTES_KO.md), and [docs/CHANGELOG_KO.md](docs/CHANGELOG_KO.md) for release details and validation notes.

## What Kavita Provides
- Serve up Manga/Webtoons/Comics (cbr, cbz, zip/rar/rar5, 7zip, raw images) and Books (epub, pdf)
- First class responsive readers that work great on any device (phone, tablet, desktop)
- Customizable theming support: [Theme Repo](https://github.com/Kareadita/Themes) and [Documentation](https://wiki.kavitareader.com/guides/themes)
- External metadata integration and scrobbling for read status, ratings, and reviews (available via [Kavita+](https://wiki.kavitareader.com/kavita+)) 
- Rich Metadata support with filtering, searching, and smart filters
- Ways to group reading material: Collections, Reading Lists (CBL Import), Want to Read
- Ability to manage users with rich Role-based management for age restrictions, abilities within the app, OIDC, etc
- Rich web readers supporting webtoon, continuous reading mode (continue without leaving the reader), virtual pages (epub), etc
- Ability to customize your dashboard and side nav with smart filters, custom order and visibility toggles
- Full Localization Support ([Weblate](https://hosted.weblate.org/engage/kavita/))
- Ability to download metadata, reviews, ratings, and more (available via [Kavita+](https://wiki.kavitareader.com/kavita+))
- Epub-based Annotation/Highlight support 

## Support
[![Discord](https://img.shields.io/badge/discord-chat-7289DA.svg?maxAge=60)](https://discord.gg/eczRp9eeem)
[![GitHub - Bugs Only](https://img.shields.io/badge/github-issues-red.svg?maxAge=60)](https://github.com/Kareadita/Kavita/issues)

## Demo
If you want to try out Kavita, a demo is available:
[https://demo.kavitareader.com/](https://demo.kavitareader.com/login?apiKey=9003cf99-9213-4206-a787-af2fe4cc5f1f)
```
Username: demouser
Password: Demouser64
```

## Setup
The easiest way to get started is to visit our Wiki which has up-to-date information on a variety of
install methods and platforms.
[https://wiki.kavitareader.com/getting-started](https://wiki.kavitareader.com/getting-started)

## Feature Requests
Got a great idea? Throw it up on [Discussions](https://github.com/Kareadita/Kavita/discussions/2529) or vote on another idea. Many great features in Kavita are driven by our community. 

## Notice
Kavita is being actively developed and should be considered beta software until the 1.0.0 release.
Kavita may be subject to changes in how the platform functions as it is being built out toward the
vision. You may lose data and have to restart. The Kavita team strives to avoid any data loss.

## Donate
If you like Kavita, have gotten good use out of it, or feel like you want to say thanks with a few bucks, feel free to donate. Money will go towards
expenses related to Kavita. Back us through [OpenCollective](https://opencollective.com/Kavita#backer). You can also use [Paypal](https://www.paypal.com/paypalme/majora2007?locale.x=en_US), however your name will not show below. Kavita+ is also an 
option which provides funding, and you get a benefit.

## Kavita+
[Kavita+](https://wiki.kavitareader.com/kavita+) is a paid subscription that offers premium features that otherwise wouldn't be feasible to include in Kavita. It is run and operated by [majora2007](https://github.com/majora2007), the creator and primary developer of Kavita.

If you are interested, you can use the promo code [`FIRSTTIME`](https://buy.stripe.com/00gcOQanFajG0hi5ko?prefilled_promo_code=FIRSTTIME) for your initial signup for a 50% discount on the first month (2$). This can be thought of as donating to Kavita's development and getting some sweet features out of it.

**If you already contribute via OpenCollective, please reach out to majora2007 for a provisioned license.**

## Localization
Thank you to [Weblate](https://hosted.weblate.org/engage/kavita/) who hosts our localization infrastructure pro bono. If you want to see Kavita in your language, please help us localize. Drop by the discord and sign up for the `Translator` role.

<a href="https://hosted.weblate.org/engage/kavita/">
<img src="https://hosted.weblate.org/widget/kavita/horizontal-auto.svg" alt="Translation status" />
</a>

## PikaPods
If you are looking to try your hand at self-hosting but lack the machine, [PikaPods](https://www.pikapods.com/pods?run=kavita) is a great service that 
allows you to easily spin up a server. 20% of app revenues are contributed back to Kavita via OpenCollective.


## Contributors

This project exists thanks to all the people who contribute and upstream library maintainers. [Contribute](CONTRIBUTING.md).
<a href="https://github.com/Kareadita/Kavita/graphs/contributors">
<img src="https://opencollective.com/kavita/contributors.svg?width=890&button=false&avatarHeight=42" />
</a>


## Backers

Thank you to all our backers! 🙏 [Become a backer](https://opencollective.com/Kavita#backer)

<img src="https://opencollective.com/kavita/backers.svg?width=890&avatarHeight=42"></a>

## Sponsors

Support this project by becoming a sponsor. Your logo will show up here with a link to your website. [Become a sponsor](https://opencollective.com/Kavita#sponsor)

<img src="https://opencollective.com/Kavita/sponsors.svg?width=890"></a>

## Mega Sponsors
<img src="https://opencollective.com/Kavita/tiers/mega-sponsor.svg?width=890"></a>

## Powered By
[![JetBrains logo.](https://resources.jetbrains.com/storage/products/company/brand/logos/jetbrains.svg)](https://jb.gg/OpenSource)

### License
* [GNU GPL v3](http://www.gnu.org/licenses/gpl.html)
* Copyright 2020-2026

