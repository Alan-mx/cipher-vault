let downloadSequence = 0;

const appendHidden = (
  form: HTMLFormElement,
  name: string,
  value: string,
): void => {
  const input = document.createElement("input");
  input.type = "hidden";
  input.name = name;
  input.value = value;
  form.append(input);
};

const ensureDownloadFrame = (): HTMLIFrameElement => {
  const existing = document.getElementById(
    "cipher-vault-export-download-target",
  );
  if (existing instanceof HTMLIFrameElement) {
    return existing;
  }

  const frame = document.createElement("iframe");
  frame.id = "cipher-vault-export-download-target";
  frame.name = `cipher-vault-export-${Date.now()}-${downloadSequence++}`;
  frame.hidden = true;
  frame.title = "CipherVault plaintext export download";
  document.body.append(frame);
  return frame;
};

export const exportApi = {
  submitPlaintextExport: (confirmation: string, csrfToken: string): void => {
    const frame = ensureDownloadFrame();
    const form = document.createElement("form");
    form.method = "POST";
    form.action = "/api/exports/plaintext";
    form.target = frame.name;
    form.hidden = true;
    appendHidden(form, "confirmation", confirmation);
    appendHidden(form, "__CipherVaultAntiforgery", csrfToken);
    document.body.append(form);
    form.submit();
    form.remove();
  },
};
