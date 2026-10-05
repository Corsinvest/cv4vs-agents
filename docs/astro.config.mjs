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
      editLink: { baseUrl: 'https://github.com/Corsinvest/cv4vs-agents/edit/master/docs/' },
      plugins: [starlightLinksValidator()],
      sidebar: [],
    }),
  ],
});
