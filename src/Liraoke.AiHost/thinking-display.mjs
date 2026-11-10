// A conservative display filter, not a secrecy or authorization boundary.
// Never forward partial sentences: prompt quotations can span arbitrary SSE chunks.
export const HIDDEN_THINKING = '［已隐藏内部指令引用］';
const normalize = text => text.normalize('NFKC').toLowerCase().replace(/[^\p{L}\p{N}]/gu, '');
const instructionReference = /\b(?:system|developer)[\s_-]*(?:prompt|message|instruction|says?|said|tells?|told|requires?|reminds?|asks?|context|role)\b|\b(?:prompt|instruction)[\s_-]*(?:system|developer)\b|系统\s*(?:提示|指令|消息|要求|说)|开发者\s*(?:提示|指令|消息|要求)|内部\s*(?:提示词|指令)/iu;

export class ThinkingDisplayFilter {
  constructor(systemPrompt, emit) {
    this.emit = emit; this.pending = ''; this.hidden = false;
    // Match recognizable prompt fragments, including quotes with altered spaces/punctuation.
    this.fragments = [];
    for (const sentence of systemPrompt.split(/[。；;！!？?\n]/u)) {
      const s = normalize(sentence); if (s.length < 8) continue;
      const width = Math.min(12, s.length);
      for (let i=0;i<=s.length-width;i++) this.fragments.push(s.slice(i,i+width));
    }
  }
  push(delta) {
    if (this.hidden) return;
    this.pending += delta;
    let boundary;
    while (!this.hidden && (boundary = /[。！？\n]|[.!?](?=\s)/u.exec(this.pending))) {
      const end=boundary.index+boundary[0].length;
      const sentence=this.pending.slice(0,end); this.pending=this.pending.slice(end);
      this.check(sentence);
    }
    // No unchecked prefixes for long, unpunctuated output either.
    if (this.pending.length > 4096) this.hide();
  }
  flush() {
    if (!this.hidden && this.pending) this.check(this.pending);
    this.pending = '';
  }
  check(sentence) {
    const s = normalize(sentence);
    if (instructionReference.test(sentence) || this.fragments.some(part=>s.includes(part))) this.hide();
    else if (sentence) this.emit(sentence);
  }
  hide() {
    if (!this.hidden) this.emit(HIDDEN_THINKING);
    // Once an internal-instruction reference is detected, do not expose its continuation.
    this.hidden = true; this.pending = '';
  }
}
