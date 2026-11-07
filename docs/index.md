---
layout: home

hero:
  name: pgembed
  text: Real PostgreSQL, any version, no Docker
  tagline: Run named Postgres instances side by side, query them with readable results, inspect their schema, and hand them to your AI assistant over MCP. One .NET tool.
  image:
    src: /logo.png
    alt: pgembed, an elephant standing on a terminal
  actions:
    - theme: brand
      text: Get Started
      link: /guide/getting-started
    - theme: alt
      text: MCP Server
      link: /guide/mcp
    - theme: alt
      text: View on GitHub
      link: https://github.com/mysticmind/pgembed-cli

features:
  - title: Any version, side by side
    details: Postgres 9.6 to 18 from the official binaries, on Windows, macOS and Linux (glibc and musl, x64 and arm64). Several versions at once, each a named instance that keeps its port.
    link: /guide/instances
    linkText: Instances
  - title: Minimal, small downloads
    details: Just the server, initdb and pg_ctl, nothing else. About 14 MB on Linux and 24 MB on Windows, against 112 to 154 MB for the official Docker image. Downloaded once into a shared cache, checksum-verified.
    link: /guide/versions-and-cache#minimal-binaries
    linkText: What you get
  - title: No Docker, no install
    details: Works on laptops and CI runners that cannot run Docker or install packages. Nothing to set up beyond the .NET SDK.
    link: /guide/introduction
    linkText: Why pgembed
  - title: SQL you can read
    details: Tables that fit the terminal, psql-style text for pipes, JSON with nested json values, CSV and Markdown. Errors point at the position, with the server's hint.
    link: /guide/sql
    linkText: Run SQL
  - title: Safe by default
    details: Statements that change the schema or existing rows are listed and confirmed before anything runs, from the CLI and from MCP. UPDATE or DELETE without WHERE is called out.
    link: /guide/sql#changes-are-confirmed-first
    linkText: How it decides
  - title: A throwaway Postgres for AI assistants
    details: pgembed mcp gives Claude, Copilot and other MCP clients 18 tools to start instances, run and explain SQL, and read schemas, with read-only mode and confirmation.
    link: /guide/mcp
    linkText: Set it up
  - title: Shared with your tests
    details: Instances live where MysticMind.PostgresEmbed looks, so a test attaches to `pgembed start app` in milliseconds, or starts it on its own.
    link: /guide/testing
    linkText: From .NET tests
  - title: Fast
    details: Precompiled per platform; a query against an instance takes about 100 ms end to end. Results stream, so a million rows use no more memory than one.
    link: /guide/performance
    linkText: The numbers
  - title: The real PostgreSQL parser
    details: Scripts split correctly around dollar quotes, syntax checks and formatting work offline, and each statement is summarized before it runs.
    link: /guide/format
    linkText: Format and check
---
