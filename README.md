# PlayWithSpv
Fullblock-SPV proof of concept implementation in .NET Core.  
  
Bloom filtering SPV wallets are volnurable against network analysis.  
This proof of concept implementation of an SPV wallet first sync the headers, then downloads full blocks from the creation of the wallet, therefore posessing the same privacy characteristics as a full-node.  
It does not store the blocks, but rather listening to the interested bitcoin addresses and transactions and store them locally then throws away the full-block and only stores the merkle block.  
