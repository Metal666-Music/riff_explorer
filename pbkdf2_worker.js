importScripts(
	"https://cdnjs.cloudflare.com/ajax/libs/crypto-js/4.2.0/crypto-js.min.js"
);

onmessage = (message) => {
	const key = CryptoJS.PBKDF2(
		message.data[0],
		CryptoJS.enc.Utf8.parse(message.data[1]),
		{
			keySize: 256 / 32,
			hasher: CryptoJS.algo.SHA512,
			iterations: 100_000,
		}
	);

	postMessage([key.words, key.sigBytes]);
};
