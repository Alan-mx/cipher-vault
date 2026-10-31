export const collectFolders = (paths: readonly string[]): string[] => {
  const folders = new Set<string>();
  for (const path of paths) {
    const segments = path.split("/");
    segments.pop();
    let current = "";
    for (const segment of segments) {
      current = current.length === 0 ? segment : `${current}/${segment}`;
      folders.add(current);
    }
  }
  return Array.from(folders).sort((left, right) =>
    left.localeCompare(right, "zh-CN"),
  );
};
