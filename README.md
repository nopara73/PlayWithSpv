Fullblock-SPV proof of concept implementation in .NET Core.  
  
## Disclaimer  
The code is a proof of concept. It has been rushcoded. Do not use it in production!  
Use the code as learning material if you want to build such a wallet.  
  
##Motivation
“What is the lightest weight wallet implementation that does not compromises the User’s privacy?” 
  
Bloom filtering SPV wallets are [volnurable](https://jonasnick.github.io/blog/2015/02/12/privacy-in-bitcoinj/) against network analysis.  
This proof of concept implementation of an SPV wallet first sync the headers, then downloads full blocks from the creation of the wallet, therefore posessing the same privacy characteristics as a full-node. But the initial sync is faster and stores less data.  
It does not store the blocks, but rather listening to the interested bitcoin addresses and transactions and store them locally then throws away the full-block and only stores the merkle block.  
  
[Read more about why this wallet structure has been chosen here.](https://medium.com/@nopara73/bitcoin-privacy-landscape-in-2017-zero-to-hero-guidelines-and-research-a10d30f1e034)  
  
##ToDo: 
-
  
##ToNotDo: 
* Store private keys.  
* Build and broadcast transactions.  
For these functions see: https://github.com/nopara73/HiddenWallet
