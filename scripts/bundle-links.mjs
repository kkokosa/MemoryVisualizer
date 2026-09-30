import { readdir, readlink, realpath } from "node:fs/promises";
import { isAbsolute, join, relative, sep } from "node:path";

export async function assertContainedSymlinks(bundle) {
  const root = await realpath(bundle);
  const directories = [root];
  while (directories.length > 0) {
    const directory = directories.pop();
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name);
      if (entry.isSymbolicLink()) {
        const target = await readlink(path);
        if (isAbsolute(target))
          throw new Error(`Bundle link must be relative for relocation: ${relative(root, path)}`);
        const resolved = relative(root, await realpath(path));
        if (isAbsolute(resolved) || resolved === ".." || resolved.startsWith(`..${sep}`))
          throw new Error(`Bundle link escapes the assembled application: ${relative(root, path)}`);
      } else if (entry.isDirectory()) {
        directories.push(path);
      }
    }
  }
}
