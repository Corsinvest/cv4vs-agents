/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// ESLint flat config (the .eslintrc format ESLint 10 no longer reads).
//
// Formatting is Prettier's job, not ESLint's: eslint-config-prettier switches off every stylistic
// rule, so what is left here only catches bugs and enforces the one architectural rule we have.

import js from '@eslint/js';
import tseslint from 'typescript-eslint';
import prettier from 'eslint-config-prettier';

export default tseslint.config(
    // Generated DTOs are TypeGen's output — linting them reports on a file nobody edits.
    { ignores: ['core/generated/**'] },

    js.configs.recommended,
    ...tseslint.configs.recommended,
    prettier,

    {
        languageOptions: {
            parserOptions: {
                // Both: the app config excludes *.test.ts (they never reach the bundle), so
                // without the test one every test file is a parse error here.
                project: ['./tsconfig.json', './tsconfig.test.json'],
                tsconfigRootDir: import.meta.dirname,
            },
        },
        rules: {
            '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_' }],
            '@typescript-eslint/no-explicit-any': 'warn',
            curly: ['error', 'all'],
            // The direction is ui → core, never the other way: core holds the state and the wire,
            // ui renders it. Enforced rather than documented, because an import in the wrong
            // direction compiles perfectly well and is only noticed much later.
            'no-restricted-imports': 'off',
            '@typescript-eslint/no-restricted-imports': [
                'error',
                {
                    patterns: [
                        {
                            group: ['**/ui/*', '**/ui'],
                            message:
                                'Files under core/ must not import from ui/. Direction is ui → core, never the other way.',
                        },
                    ],
                },
            ],
        },
    },

    // ui/ is the other side of that boundary: importing from core is exactly what it is meant to do.
    {
        files: ['ui/**/*.ts', 'index.ts'],
        rules: { '@typescript-eslint/no-restricted-imports': 'off' },
    },
);
