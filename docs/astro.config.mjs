/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import starlightLinksValidator from 'starlight-links-validator';
import { externalLinksInNewTab, socialLinksInNewTab } from './external-links.mjs';

export default defineConfig({
  site: 'https://corsinvest.github.io',
  base: '/cv4vs-agents',
  integrations: [
    starlight({
      title: 'cv4vs Agents',
      description: 'Claude Code inside Visual Studio 2022 and 2026: a rich chat, a real terminal and the IDE handed to the agent as tools.',
      logo: { src: './src/assets/mascot.svg', alt: '' },
      favicon: '/icon.svg',
      customCss: ['./src/styles/vs2026.css'],
      social: [
        { icon: 'github', label: 'GitHub', href: 'https://github.com/Corsinvest/cv4vs-agents' },
      ],
      head: [socialLinksInNewTab],
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
