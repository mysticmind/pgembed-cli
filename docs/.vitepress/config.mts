import { defineConfig } from 'vitepress'
import { fileURLToPath, URL } from 'node:url'
import { regionSnippetPlugin } from '@radarleaf/markdown-it-region-snippets'

// Repo root, so the snippet plugin can scan the samples and include generated reference files.
const rootDir = fileURLToPath(new URL('../..', import.meta.url))

// Deployed to GitHub Pages at https://mysticmind.github.io/pgembed-cli/
export default defineConfig({
  title: 'pgembed',
  description: 'Real PostgreSQL of any version, locally, without Docker: instances, SQL, schema, and an MCP server.',
  base: '/pgembed-cli/',
  lastUpdated: true,
  cleanUrls: true,
  markdown: {
    // Expand `snippet: sample_*` markers from `# region` / `#region` blocks in samples/ (shell scripts CI runs,
    // C# samples CI tests, config files), so the docs always show code that works.
    // https://www.npmjs.com/package/@radarleaf/markdown-it-region-snippets
    config(md) {
      md.use(regionSnippetPlugin, {
        rootDir,
        dirs: ['samples'],
        include: /^sample_/,
        syntax: 'snippet-colon',
        // an anchor and a "source" link under each snippet, pointing at the sample on GitHub
        urlPrefix: 'https://github.com/mysticmind/pgembed-cli/blob/main',
      })
    },
  },
  head: [
    ['link', { rel: 'icon', href: '/pgembed-cli/logo.png' }],
  ],
  themeConfig: {
    logo: '/logo.png',
    nav: [
      { text: 'Guide', link: '/guide/getting-started' },
      { text: 'MCP', link: '/guide/mcp' },
      { text: 'Reference', link: '/reference/commands' },
      { text: 'NuGet', link: 'https://www.nuget.org/packages/pgembed/' },
    ],
    sidebar: [
      {
        text: 'Guide',
        items: [
          { text: 'Introduction', link: '/guide/introduction' },
          { text: 'Getting Started', link: '/guide/getting-started' },
          { text: 'Instances', link: '/guide/instances' },
          { text: 'SQL', link: '/guide/sql' },
          { text: 'Schema', link: '/guide/schema' },
          { text: 'Format and Check', link: '/guide/format' },
          { text: 'Versions and Cache', link: '/guide/versions-and-cache' },
        ],
      },
      {
        text: 'Using it',
        items: [
          { text: 'From .NET Tests and Apps', link: '/guide/testing' },
          { text: 'In CI', link: '/guide/ci' },
          { text: 'With AI Assistants (MCP)', link: '/guide/mcp' },
          { text: 'Performance', link: '/guide/performance' },
        ],
      },
      {
        text: 'Reference',
        items: [
          { text: 'Commands', link: '/reference/commands' },
          { text: 'MCP Tools', link: '/reference/mcp-tools' },
          { text: 'Configuration', link: '/reference/configuration' },
        ],
      },
    ],
    socialLinks: [
      { icon: 'github', link: 'https://github.com/mysticmind/pgembed-cli' },
    ],
    search: { provider: 'local' },
    editLink: {
      pattern: 'https://github.com/mysticmind/pgembed-cli/edit/main/docs/:path',
      text: 'Edit this page on GitHub',
    },
    footer: {
      message: 'Released under the MIT License.',
      copyright: 'Copyright © Babu Annamalai',
    },
  },
})
