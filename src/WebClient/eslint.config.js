import js from "@eslint/js";
import configPrettier from "eslint-config-prettier";
import reactHooks from "eslint-plugin-react-hooks";
import reactRefresh from "eslint-plugin-react-refresh";
import globals from "globals";
import tseslint from "typescript-eslint";

export default tseslint.config(
    // schema.d.ts is generated from the Server's OpenAPI contract (npm run gen:api).
    { ignores: ["dist", "coverage", "src/serverApi/schema.d.ts"] },
    {
        files: ["**/*.{ts,tsx}"],
        extends: [
            js.configs.recommended,
            tseslint.configs.recommendedTypeChecked,
            reactHooks.configs.flat["recommended-latest"],
            reactRefresh.configs.vite,
        ],
        languageOptions: {
            globals: globals.browser,
            parserOptions: {
                projectService: true,
                tsconfigRootDir: import.meta.dirname,
            },
        },
    },
    // Last: drop stylistic rules that would fight Prettier.
    configPrettier,
);
