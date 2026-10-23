import { readFile, readdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const source = path.join(root, 'src/ShoppyShop.Infrastructure/Data/Translations');
const configuration = JSON.parse(await readFile(path.join(root, 'src/ShoppyShop.Api/appsettings.json'), 'utf8'));
const catalogue = JSON.parse(await readFile(path.join(root, 'src/ShoppyShop.Infrastructure/Data/catalogue.json'), 'utf8'));
const sourceLocale = configuration.Localization.DefaultLocale;
const translations = await Promise.all((await readdir(source)).filter((file) => file.endsWith('.json')).sort()
  .map(async (file) => JSON.parse(await readFile(path.join(source, file), 'utf8'))));

// Full text, for the frontend's prerendering repositories (server-only).
const text = JSON.stringify({ sourceLocale, translations }, null, 2) + '\n';

// Display names only, for the browser bundle: every locale's name, so a basket saved in one language
// is recognised in another. A translation of out-of-date source text is left out, as the API does.
const current = {
  groups: (group, t) => t.sourceName === group.name && t.sourceDescription === group.description
    && t.sourceBadge === group.badge,
  products: (product, t) => t.sourceName === product.name && t.sourceDescription === product.description,
};
const localizedNames = (kind, entity) => Object.fromEntries([
  [sourceLocale, entity.name],
  ...translations.flatMap((translation) => {
    const match = translation[kind].find((t) => t.id === entity.id && current[kind](entity, t));
    return match ? [[translation.locale, match.name]] : [];
  }),
]);
const names = JSON.stringify({
  sourceLocale,
  groups: catalogue.groups.map((group) => ({ id: group.id, name: localizedNames('groups', group) })),
  products: catalogue.products.map((product) => ({ id: product.id, name: localizedNames('products', product) })),
}, null, 2) + '\n';

const target = path.resolve(process.argv.find((argument) => argument.startsWith('--output='))?.slice(9)
  ?? path.join(root, '../shoppy-shop/src/app/features/catalogue/data'));
const outputs = [
  [path.join(target, 'catalogue.translations.json'), text],
  [path.join(target, 'catalogue.names.json'), names],
];
if (process.argv.includes('--check')) {
  for (const [file, output] of outputs) {
    const existing = JSON.parse(await readFile(file, 'utf8'));
    if (JSON.stringify(existing) !== JSON.stringify(JSON.parse(output))) {
      throw new Error(`${path.basename(file)} is stale. Run the export script.`);
    }
  }
  console.log('Catalogue translation snapshots match the API source.');
} else {
  for (const [file, output] of outputs) {
    await writeFile(file, output);
    console.log('Exported ' + file);
  }
}
