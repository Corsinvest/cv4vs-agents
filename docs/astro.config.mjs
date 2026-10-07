/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import starlightLinksValidator from 'starlight-links-validator';
import { externalLinksInNewTab, socialLinksInNewTab } from './external-links.mjs';
import { matomoHead } from './matomo.mjs';

// Only in the built site: local dev visits stay out of the statistics.
// Starlight emits og:title and og:description but no image, and the twitter:card it declares is
// the large-image one: without this a shared link is a bare line of text. An absolute URL,
// because the scrapers that read it resolve nothing relative.
const ogImage = 'https://corsinvest.github.io/cv4vs-agents/og.png';
const socialCard = [
  { tag: 'meta', attrs: { property: 'og:image', content: ogImage } },
  { tag: 'meta', attrs: { property: 'og:image:width', content: '1200' } },
  { tag: 'meta', attrs: { property: 'og:image:height', content: '630' } },
  { tag: 'meta', attrs: { property: 'og:image:alt', content: 'cv4vs Agents: Claude Code inside Visual Studio' } },
  { tag: 'meta', attrs: { name: 'twitter:image', content: ogImage } },
];

const matomo = process.argv.includes('build')
  ? [matomoHead({ url: 'https://matomo.corsinvest.it/', siteId: 15 })]
  : [];

export default defineConfig({
  site: 'https://corsinvest.github.io',
  base: '/cv4vs-agents',
  integrations: [
    starlight({
      title: 'cv4vs Agents',
      description: 'Claude Code inside Visual Studio 2022 and 2026: a rich chat, a real terminal and the IDE handed to the agent as tools.',
      logo: { src: './src/assets/mascot.svg', alt: '' },
      favicon: '/icon.svg',
      customCss: ['./src/styles/vs2026.css', './src/styles/corsinvest-link.css'],
      social: [
        { icon: 'github', label: 'GitHub', href: 'https://github.com/Corsinvest/cv4vs-agents' },
        // The icon is a stand-in: corsinvest-link.css draws the Corsinvest mark over it.
        { icon: 'external', label: 'Corsinvest', href: 'https://www.corsinvest.it' },
      ],
      head: [socialLinksInNewTab, ...socialCard, ...matomo],
      editLink: { baseUrl: 'https://github.com/Corsinvest/cv4vs-agents/edit/master/docs/' },
      plugins: [starlightLinksValidator()],
      sidebar: [
        { label: 'Start here', items: ['getting-started', 'two-panes', 'why', 'known-issues'] },
        {
          label: 'Guides',
          items: [
            'guides/debug-with-the-agent',
            'guides/ask-from-the-editor',
            'guides/teach-the-agent',
            'guides/another-provider',
            'guides/several-panes',
            'guides/spending-less-context',
          ],
        },
        {
          label: 'Chat',
          items: [
            'chat/composer',
            'chat/conversation',
            'chat/permissions',
            'chat/sessions',
            'chat/diff',
            'chat/rewind',
            'chat/sub-agents',
            'chat/queued-messages',
            'chat/file-links',
            'chat/context-and-usage',
          ],
        },
        { label: 'IDE tools', items: ['ide-integration', 'mcp-tools'] },
        {
          label: 'Documents',
          items: ['documents/statistics', 'documents/usage', 'documents/context-usage', 'documents/file-history'],
        },
        {
          label: 'Claude Code',
          items: [
            'claude/authentication',
            'claude/updating',
            'claude/plugins',
            'claude/remote-control',
            'claude/vs-code-differences',
          ],
        },
        { label: 'Reference', items: ['options', 'settings-and-data', 'power', 'troubleshooting', 'architecture'] },
        { label: 'Project', items: ['diary'] },
        {
          label: 'Corsinvest',
          items: [
            {
              label: 'Professional support',
              link: 'https://www.corsinvest.it/en/contact/',
              attrs: { target: '_blank', rel: 'noopener noreferrer' },
            },
          ],
        },
      ],
    }),
    // After Starlight: it reads the Markdown processor Starlight has configured.
    externalLinksInNewTab(),
  ],
});
