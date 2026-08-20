// @ts-check
// Note: type annotations allow type checking and IDEs autocompletion

const {themes} = require('prism-react-renderer');

const lightCodeTheme = themes.github;
const darkCodeTheme = themes.dracula;

/** @type {import('@docusaurus/types').Config} */
const config = {
  title: 'UdonSharp CE',
  tagline: 'Higher-level C# for VRChat Udon',
  url: 'https://vrc.furroxide.dev',
  baseUrl: '/',
  onBrokenLinks: 'throw',
  markdown: {
    hooks: {
      onBrokenMarkdownLinks: 'warn',
    },
  },
  favicon: 'images/favicon.ico',
  organizationName: 'Furroxide',
  projectName: 'UdonSharpCE',

  presets: [
    [
      'classic',
      /** @type {import('@docusaurus/preset-classic').Options} */
      ({
        docs: {
          routeBasePath: '/',
          sidebarPath: require.resolve('./sidebars.js'),
          // Please change this to your repo.
          editUrl: ({versionDocsDirPath, docPath}) =>
              `https://github.com/Furroxide/UdonSharpCE/edit/master/Tools/Docusaurus/docs/${docPath}`,
          
        },
        blog: {
          path: 'news',
          routeBasePath: 'news',
          showReadingTime: false,
          onUntruncatedBlogPosts: 'ignore',
          // Please change this to your repo.
          // editUrl: 'https://github.com/Furroxide/UdonSharpCE',
        },
        theme: {
          customCss: require.resolve('./src/css/custom.css'),
        },
      }),
    ],
  ],

  themeConfig:
    /** @type {import('@docusaurus/preset-classic').ThemeConfig} */
    ({
      colorMode: {
        defaultMode: 'dark',
        respectPrefersColorScheme: true,
      },
      navbar: {
        title: 'UdonSharp CE',
        logo: {
          alt: 'UdonSharp CE',
          src: 'images/logo.png',
        },
        items: [
          {
            type: 'doc',
            docId: 'index',
            position: 'left',
            label: 'Docs',
          },
          {to: '/news', label: 'News', position: 'left'},
          {
            href: 'https://github.com/Furroxide/UdonSharpCE',
            label: 'GitHub',
            position: 'right',
          },
        ],
      },
      footer: {
        style: 'dark',
        links: [
          {
            title: 'Docs',
            items: [
              {
                label: 'Docs',
                to: '/',
              },
            ],
          },
          {
            title: 'Upstream',
            items: [
              {
                label: 'UdonSharp by Merlin',
                href: 'https://github.com/MerlinVR/UdonSharp',
              },
            ],
          },
          {
            title: 'More',
            items: [
              {
                label: 'News',
                to: '/news',
              },
              {
                label: 'GitHub',
                href: 'https://github.com/Furroxide/UdonSharpCE',
              },
            ],
          },
        ],
        copyright: `Copyright © ${new Date().getFullYear()} UdonSharp CE Contributors. UdonSharp is © Merlin and VRChat Inc. Built with Docusaurus.`,
      },
      prism: {
        theme: lightCodeTheme,
        darkTheme: darkCodeTheme,
      },
    }),
};

module.exports = config;
