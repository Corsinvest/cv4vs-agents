// Ambient module declarations for non-TS imports handled by esbuild.

declare module '*.svg' {
    const svg: string;
    export default svg;
}

declare module '*.css' {
    const css: string;
    export default css;
}
