/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import starlightLinksValidator from 'starlight-links-validator';

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
      // Starlight has no option for this, and its social links take no target at all: one script
      // covers every link that leaves the site, whichever component rendered it.
      head: [
        {
          tag: 'script',
          content:
            `document.addEventListener('DOMContentLoaded',()=>{document.querySelectorAll('a[href]').forEach((a)=>{` +
            `if(/^https?:$/.test(a.protocol)&&a.origin!==location.origin){a.target='_blank';a.rel=(a.rel+' noopener noreferrer').trim();}});});`,
        },
      ],
      editLink: { baseUrl: 'https://github.com/Corsinvest/cv4vs-agents/edit/master/docs/' },
      plugins: [starlightLinksValidator()],
      sidebar: [],
    }),
  ],
});
