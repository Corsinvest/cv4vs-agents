/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 *
 * Links that leave the site open in a new tab, so the reader keeps the page they were on.
 * Same approach as @corsinvest/cv4pve-docs-theme: written into the HTML at build time, so it
 * works without JavaScript; a script is used only where Starlight leaves no other way.
 */

import { isSatteriProcessor, satteri } from '@astrojs/markdown-satteri';

/**
 * Astro integration: absolute http(s) links in Markdown that are not under the site root get
 * target and rel. "Under the site root" and not "same origin": every Corsinvest documentation
 * site shares corsinvest.github.io, and another project's site is a different site.
 */
export function externalLinksInNewTab() {
  return {
    name: 'external-links-new-tab',
    hooks: {
      /** @param {any} options */
      'astro:config:setup'({ config, updateConfig }) {
        const siteRoot = new URL(config.base ?? '/', config.site ?? 'http://localhost').href;
        const plugin = {
          name: 'external-links-new-tab',
          element: {
            filter: ['a'],
            /** @param {any} node @param {any} ctx */
            visit(node, ctx) {
              const href = node.properties?.href;
              if (typeof href !== 'string' || !/^https?:\/\//.test(href) || href.startsWith(siteRoot)) return;
              ctx.setProperty(node, 'target', '_blank');
              ctx.setProperty(node, 'rel', ['noopener', 'noreferrer']);
            },
          },
        };
        // Astro 7 renders Markdown with Satteri, which ignores markdown.rehypePlugins. Its options
        // are read by reference, so adding to the existing processor keeps what is already there.
        const processor = config.markdown.processor;
        if (isSatteriProcessor(processor)) {
          processor.options.hastPlugins = [...(processor.options.hastPlugins ?? []), plugin];
        } else {
          updateConfig({ markdown: { processor: satteri({ hastPlugins: [plugin] }) } });
        }
      },
    },
  };
}

/**
 * Head entry for the header's social links: Starlight's social config has no target option and
 * the component is not overridden. Its links are the only ones with rel="me".
 */
export const socialLinksInNewTab = {
  tag: 'script',
  content:
    `document.addEventListener('DOMContentLoaded',()=>{document.querySelectorAll('a[rel~="me"][href^="http"]')` +
    `.forEach((a)=>{a.target='_blank';a.rel='me noopener noreferrer';a.title||=a.textContent.trim();});});`,
};
