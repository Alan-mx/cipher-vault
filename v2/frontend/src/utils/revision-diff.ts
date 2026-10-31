import { createTwoFilesPatch } from "diff";
import type { RevisionContent } from "../types/api";

const MaxDiffCharacters = 200_000;

export const createRevisionDiff = (
  left: RevisionContent,
  right: RevisionContent,
): string => {
  const patch = createTwoFilesPatch(
    `R${left.revision} ${left.virtualPath}`,
    `R${right.revision} ${right.virtualPath}`,
    left.content,
    right.content,
    "",
    "",
    { context: 3 },
  );
  return patch.length <= MaxDiffCharacters
    ? patch
    : `${patch.slice(0, MaxDiffCharacters)}\n\n… diff 已截断 …`;
};
