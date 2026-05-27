# Website

This website is built using [Docusaurus](https://docusaurus.io/), a modern static website generator. The published site lives at <https://ODumpling.github.io/Kagura/>.

## Maintainer note: enable GitHub Pages

Before the deploy workflow can publish anything, a repo admin must enable Pages in **Settings → Pages → Build and deployment → Source: GitHub Actions** (not the legacy "Deploy from a branch" option). Without this, the workflow will run green but the site will return 404.

## Installation

```bash
npm install
```

## Local Development

```bash
npm run start
```

This command starts a local development server and opens up a browser window. Most changes are reflected live without having to restart the server.

## Build

```bash
npm run build
```

This command generates static content into the `build` directory and can be served using any static contents hosting service.

## Deployment

Deployment to GitHub Pages happens automatically on push to `main` via GitHub Actions. To run a manual deploy from a local checkout:

```bash
GIT_USER=<Your GitHub username> npm run deploy
```
