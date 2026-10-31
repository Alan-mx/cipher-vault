export const passwordScalarLength = (value: string): number =>
  Array.from(value).length;

export const isValidNewPassword = (value: string): boolean =>
  passwordScalarLength(value) >= 12;
