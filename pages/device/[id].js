import Layout from '../../components/Layout';
import dataservice from '../../services/dataservice';

const Post = props => (
    <Layout>
        <h1>Device</h1>
        <ul>
            {props.leases.map(lease => (
            <li key={lease.mac}>
                {lease.mac} - {lease.ip} - {lease.host} - {lease.start} - {lease.end}
            </li>
            ))}
        </ul>
  </Layout>
);

Post.getInitialProps = async function(context) {
  const { id } = context.query;
  const data = await dataservice.getLeasesByMac(id);

  console.log(`Fetched device: ${id}`);

  return { leases: data };
};

export default Post;