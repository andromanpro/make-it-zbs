"use strict";

module.exports = [
    {
        files: ["ui/*.js", "integrations/vscode/*.js", "test/javascript/*.js", "eslint.config.js"],
        languageOptions: {
            ecmaVersion: 2022,
            sourceType: "script",
            globals: {
                confirm: "readonly",
                document: "readonly",
                globalThis: "readonly",
                module: "readonly",
                require: "readonly",
                window: "readonly"
            }
        },
        rules: {
            "curly": ["error", "all"],
            "eqeqeq": ["error", "always"],
            "no-eval": "error",
            "no-implied-eval": "error",
            "no-unused-vars": ["error", { "argsIgnorePattern": "^_" }],
            "no-var": "error",
            "prefer-const": "error"
        }
    }
];
