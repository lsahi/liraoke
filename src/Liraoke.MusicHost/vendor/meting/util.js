export function format(lyric, tlyric) {
  const lyricArray = trimLyric(lyric)
  const tlyricArray = trimLyric(tlyric)
  if (tlyricArray.length === 0) {
    return lyric
  }
  const result = []
  for (let i = 0, j = 0; i < lyricArray.length && j < tlyricArray.length; i += 1) {
    const time = lyricArray[i].time
    let text = lyricArray[i].text
    while (time > tlyricArray[j].time && j + 1 < tlyricArray.length) {
      j += 1
    }
    if (time === tlyricArray[j].time && tlyricArray[j].text.length) {
      text = `${text} (${tlyricArray[j].text})`
    }
    result.push({
      time,
      text
    })
  }
  return result
    .map(x => {
      const minus = Math.floor(x.time / 60000).toString().padStart(2, '0')
      const second = Math.floor((x.time % 60000) / 1000).toString().padStart(2, '0')
      const millisecond = Math.floor((x.time % 1000)).toString().padStart(3, '0')
      return `[${minus}:${second}.${millisecond}]${x.text}`
    })
    .join('\n')
}

const trimLyric = (lyric) => {
  const result = []
  const lines = lyric.split('\n')
  for (const line of lines) {
    const match = line.match(/^\[(\d{2}):(\d{2}\.\d*)\](.*)$/)
    if (match) {
      result.push({
        time: parseInt(parseInt(match[1], 10) * 60 * 1000 + parseFloat(match[2]) * 1000),
        text: match[3]
      })
    }
  }
  return result.sort((a, b) => a.time - b.time)
}

const getPathFromURL = (url, strict = true) => {
  const queryIndex = url.indexOf("?");
  const result = url.substring(url.indexOf("/", 8), queryIndex === -1 ? url.length : queryIndex);
  if (strict === false && result.endsWith("/")) {
    return result.slice(0, -1);
  }
  return result;
};

export const get_runtime = () => {

  if (globalThis?.process?.env?.RUNTIME) {
    return globalThis?.process?.env?.RUNTIME
  }

  if (globalThis?.Deno !== undefined) {
    return 'deno'
  }

  if (globalThis?.Bun !== undefined) {
    return 'bun'
  }

  if (typeof globalThis?.WebSocketPair === 'function') {
    return 'cloudflare'
  }

  if (globalThis?.fastly !== undefined) {
    return 'fastly'
  }

  if (typeof globalThis?.EdgeRuntime === 'string') {
    return 'vercel'
  }

  if (globalThis?.process?.release?.name === 'node') {
    return 'node'
  }

  if (globalThis?.__lagon__ !== undefined) {
    return 'lagon'
  }

  return 'other'
}

export const get_url = (ctx) => {
  const runtime = get_runtime()
  const rawPath = getPathFromURL(ctx.req.url.split('?')[0])

  // 子路径反代（如 /meting）可选手动指定完整对外基址
  const forwardedBase = String(
    ctx.req.header('X-Forwarded-Host') || ctx.req.header('X-Forwarded-Url') || '',
  ).trim().replace(/\/+$/, '')

  const forwardedProto = String(ctx.req.header('X-Forwarded-Proto') || '').split(',')[0].trim().toLowerCase()
  const host = String(ctx.req.header('Host') || '').trim()

  let req_url = ''
  if (forwardedBase) {
    req_url = forwardedBase + rawPath
  } else if (host) {
    const scheme = forwardedProto === 'https' ? 'https' : 'http'
    const prefix = String(ctx.req.header('X-Forwarded-Prefix') || '').trim().replace(/\/+$/, '')
    const origin = `${scheme}://${host}${prefix}`
    req_url = `${origin}${rawPath}`
  } else {
    req_url = ctx.req.url.split('?')[0]
  }

  if (!req_url.startsWith('http')) {
    req_url = `${forwardedProto === 'https' ? 'https' : 'http'}://${req_url}`
  }
  if (runtime === 'vercel') req_url = req_url.replace('http://', 'https://')
  return req_url
}

/** 对外基址（scheme + host + 子路径前缀），用于拼 /audio/qishui 等绝对链接 */
export const get_public_base = (ctx) => {
  const full = get_url(ctx)
  const endpoint = new URL(full)
  const dir = endpoint.pathname.replace(/\/[^/]*$/, '')
  endpoint.pathname = dir || '/'
  endpoint.search = ''
  endpoint.hash = ''
  return endpoint.toString().replace(/\/$/, '')
}
