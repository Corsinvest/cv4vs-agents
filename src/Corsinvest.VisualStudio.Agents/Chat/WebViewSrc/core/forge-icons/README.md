# Brand marks

The marks shown ahead of a link to a git hosting service (`core/forge-links.ts`,
`core/markdown.ts`).

Copied from [Simple Icons](https://simpleicons.org) 16.34.0, one file per service, unchanged. The
collection is CC0. The marks themselves remain trademarks of their owners: they are used here only
to say which service a link goes to, and must not be redrawn or recoloured beyond the single
colour they take from the text around them.

To add one: copy the file from the same version of `simple-icons`, import it in
`core/markdown.ts`, and map the provider to it in `FORGE_ICON`.

One exception to the version: `azuredevops.svg` is from Simple Icons 12.4.0. Later versions no
longer carry Microsoft's marks, so there is no current file to copy.

Not here on purpose:

- Gerrit: the drawing is about 10 KB and would be repeated in every link. Plain Git mark too.
