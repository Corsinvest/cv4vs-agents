/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

/**
 * Head script: Matomo page views and outbound links. Cookies off, so visits are counted without
 * recognising the visitor and no consent banner is required; IP anonymisation is set on the
 * Matomo server. Same script as @corsinvest/cv4pve-docs-theme writes for the cv4pve sites.
 * @param {{ url: string, siteId: number }} matomo
 */
export function matomoHead({ url, siteId }) {
  const base = url.endsWith('/') ? url : `${url}/`;
  return {
    tag: /** @type {const} */ ('script'),
    content:
      `var _paq=window._paq=window._paq||[];_paq.push(['disableCookies']);_paq.push(['trackPageView']);` +
      `_paq.push(['enableLinkTracking']);(function(){var u=${JSON.stringify(base)};` +
      `_paq.push(['setTrackerUrl',u+'matomo.php']);_paq.push(['setSiteId',${JSON.stringify(String(siteId))}]);` +
      `var d=document,g=d.createElement('script'),s=d.getElementsByTagName('script')[0];` +
      `g.async=true;g.src=u+'matomo.js';s.parentNode.insertBefore(g,s);})();`,
  };
}
