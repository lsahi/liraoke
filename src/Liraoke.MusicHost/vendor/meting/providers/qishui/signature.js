export const buildQishuiSignatureHeaderList = (headers = {}) => Object.entries(headers)
    .filter(([name]) => !/^(cookie|host|content-length|x-helios|x-medusa)$/i.test(name))
    .flatMap(([name, value]) => [name.toLowerCase(), String(value)])

export const buildQishuiSignerPayload = ({ url, headers, deviceId }) => ({
    deviceId: String(deviceId),
    url: String(url),
    headers: buildQishuiSignatureHeaderList(headers),
})

export const parseQishuiSignatureHeaders = raw => {
    const parts = String(raw || '').split('\r\n').filter(Boolean)
    const headers = {}
    for (let index = 0; index + 1 < parts.length; index += 2) headers[parts[index]] = parts[index + 1]
    return headers
}

export const validateSignerEndpoint = value => {
    let url; try { url = new URL(String(value || '').trim()) } catch { throw new Error('SIGNER_ENDPOINT') }
    const loopback = url.hostname === 'localhost' || url.hostname === '[::1]' || /^127\.\d+\.\d+\.\d+$/.test(url.hostname)
    if (!(url.protocol === 'https:' || url.protocol === 'http:' && loopback) || url.username || url.password || url.hash)
        throw new Error('SIGNER_ENDPOINT')
    return url.href
}
const validateResult = result => {
    for (const key of ['X-Helios', 'X-Medusa'])
        if (typeof result?.[key] !== 'string' || !result[key].trim() || result[key].length > 16384 || /[\r\n]/.test(result[key]))
            throw new Error('SIGNER_PROTOCOL')
    return { 'X-Helios': result['X-Helios'], 'X-Medusa': result['X-Medusa'] }
}
const signRemotely = async (endpoint, payload) => {
    const url = validateSignerEndpoint(endpoint)
    try {
        const response = await fetch(url, {
            method: 'POST', redirect: 'manual', signal: AbortSignal.timeout(15000),
            headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
            body: JSON.stringify(payload),
        })
        if (!response.ok) {
            await response.body?.cancel()
            throw new Error([401,403].includes(response.status) ? 'SIGNER_AUTH'
                : response.status >= 300 && response.status < 400 ? 'SIGNER_REDIRECT' : 'SIGNER_HTTP')
        }
        const reader = response.body?.getReader(); if (!reader) throw new Error('SIGNER_PROTOCOL')
        let size = 0; const chunks = []
        try {
            for (;;) {
                const {done,value} = await reader.read(); if(done) break
                size += value.length
                if(size > 65536) { await reader.cancel(); throw new Error('SIGNER_PROTOCOL') }
                chunks.push(Buffer.from(value))
            }
        } finally { reader.releaseLock() }
        let result; try { result = JSON.parse(Buffer.concat(chunks).toString('utf8')) } catch { throw new Error('SIGNER_PROTOCOL') }
        if (result?.ok !== true) throw new Error('SIGNER_PROTOCOL')
        return validateResult(result)
    } catch (error) {
        if (error?.message?.startsWith('SIGNER_')) throw error
        throw new Error(['TimeoutError','AbortError'].includes(error?.name) ? 'SIGNER_TIMEOUT' : 'SIGNER_NETWORK')
    }
}

// Contract probe only. It does not send login state or request a song from Qishui.
export const checkQishuiSigner = async endpoint => {
    await signRemotely(endpoint, buildQishuiSignerPayload({
        deviceId: '1000000000000000', url: 'https://api.qishui.com/luna/pc/track_v2',
        headers: { Accept: 'application/json', 'User-Agent': 'LunaPC/3.0.0(290101097)' },
    }))
    return true
}

export const isQishuiRemoteSignerConfigured = signerUrl => Boolean(String(signerUrl || process.env.QISHUI_SIGNER_URL || '').trim())

export const generateQishuiSignatureHeaders = async ({ url, headers, deviceId, signerUrl = '' }) => {
    const payload = buildQishuiSignerPayload({ url, headers, deviceId })
    const endpoint = String(signerUrl || process.env.QISHUI_SIGNER_URL || '').trim()
    if (!endpoint) throw new Error('汽水签名接口未配置')
    return signRemotely(endpoint, payload)
}
